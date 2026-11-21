using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Drawing=System.Drawing;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinForms=System.Windows.Forms;

namespace CharliVision;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _host;
    private readonly AppSettings _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _saveTimer=new(){Interval=TimeSpan.FromMilliseconds(330)};
    private readonly SmoothScrollController _scroll;
    private readonly bool _suppressPersistenceForQa;
    private VisionAction? _recording;
    private CancellationTokenSource? _alignmentCancel;
    private LocalAlignmentService.Preview? _alignmentPreview;
    private string _alignmentStatus="Bereit – nur eigene, lokal ausgewählte Dateien";
    private string _alignmentLanguage="en";
    private TextBlock? _alignmentStatusText;
    private bool _alignmentBusy;
    private string _section="Playback";
    private ChromaticTokens _lastTokens;
    private bool _hasTokens;
    private byte _glassTintAlpha=210;
    public SettingsWindow(MainWindow host,AppSettings settings,HotkeyManager hotkeys,
        bool suppressPersistenceForQa=false)
    {
        InitializeComponent();
        _host=host;_settings=settings;_hotkeys=hotkeys;
        _suppressPersistenceForQa=suppressPersistenceForQa;
        _scroll=new SmoothScrollController(SettingsScroller,()=>_settings.ReducedMotion);
        SourceInitialized+=(_,_)=>{
            // Layered WPF supplies REAL per-pixel alpha at rounded window corners.
            // Native DWM backdrop attributes do not compose layered WPF windows.
            FitScreenBounds();
            CaptureBackdrop();
        };
        SettingsGlassContent.SizeChanged+=(_,e)=>{
            if(e.NewSize.Width<=0||e.NewSize.Height<=0)return;
            var clip=new RectangleGeometry(new Rect(0,0,
                e.NewSize.Width,e.NewSize.Height),22,22);
            clip.Freeze();
            SettingsGlassContent.Clip=clip;
        };
        _host.ChromaticPaletteChanged+=OnPaletteChanged;
        _saveTimer.Tick+=(_,_)=>{
            _saveTimer.Stop();
            if(!_suppressPersistenceForQa)SettingsStore.Save(_settings);
        };
        Loaded+=(_,_)=>{
            ApplyPalette();Select("Playback");Keyboard.Focus(this);
        };
        Closed+=(_,_)=>{
            _alignmentCancel?.Cancel();
            _host.ChromaticPaletteChanged-=OnPaletteChanged;
            _scroll.Dispose();
            _saveTimer.Stop();
            if(!_suppressPersistenceForQa)SettingsStore.Save(_settings);
        };
        PreviewKeyDown+=KeyPreview;
    }
    internal void SavePreview(string path)
    {
        // Explicit QA mode only: render this settings window, not the desktop.
        if(!IsLoaded)throw new InvalidOperationException("Settings not loaded");
        UpdateLayout();
        var dpi=VisualTreeHelper.GetDpi(this);
        int width=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX);
        int height=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        if(width<700||height<500||width>1800||height>1300)
            throw new InvalidOperationException("Invalid preview dimensions");
        var bitmap=new RenderTargetBitmap(width,height,
            96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream=File.Create(path);
        encoder.Save(stream);
    }
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr bitmap);
    private void FitScreenBounds()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        var area=WinForms.Screen.FromHandle(hwnd).WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(this);
        MaxWidth=Math.Max(540,(area.Width-20)/Math.Max(.5,dpi.DpiScaleX));
        MaxHeight=Math.Max(390,(area.Height-20)/Math.Max(.5,dpi.DpiScaleY));
        MinWidth=Math.Min(750,MaxWidth);
        MinHeight=Math.Min(470,MaxHeight);
        Width=Math.Min(1120,MaxWidth);
        Height=Math.Min(760,MaxHeight);
        Left=area.Left/dpi.DpiScaleX+(area.Width/dpi.DpiScaleX-Width)/2;
        Top=area.Top/dpi.DpiScaleY+(area.Height/dpi.DpiScaleY-Height)/2;
    }
    private void CaptureBackdrop()
    {
        // One private in-memory frosted snapshot for the settings window.
        // Never polls the desktop or writes the background to a file.
        try{
            var dpi=VisualTreeHelper.GetDpi(this);
            int w=(int)Math.Ceiling(Width*dpi.DpiScaleX);
            int h=(int)Math.Ceiling(Height*dpi.DpiScaleY);
            if(w<500||h<400||w>2000||h>1600)return;
            var point=PointToScreen(new Point(0,0));
            using var bmp=new Drawing.Bitmap(w,h,
                Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var gfx=Drawing.Graphics.FromImage(bmp))
                gfx.CopyFromScreen((int)point.X,(int)point.Y,0,0,
                    new Drawing.Size(w,h),Drawing.CopyPixelOperation.SourceCopy);
            double average=0;
            for(int y=1;y<=3;y++)for(int x=1;x<=3;x++){
                var pixel=bmp.GetPixel(w*x/4,h*y/4);
                average+=.2126*pixel.R+.7152*pixel.G+.0722*pixel.B;
            }
            // A single translucent tint remains inside the rounded clip.
            // The desktop image stays visible without obscuring the controls.
            _glassTintAlpha=(byte)(average/9>145?147:99);
            IntPtr handle=bmp.GetHbitmap();
            try{
                var source=Imaging.CreateBitmapSourceFromHBitmap(
                    handle,IntPtr.Zero,Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                GlassBackdrop.Source=source;
            }finally{DeleteObject(handle);}
        }catch(Exception){
            GlassBackdrop.Source=null;
            _glassTintAlpha=166;
        }
    }
    private void SaveSoon()
    {
        if(!_suppressPersistenceForQa){
            _saveTimer.Stop();_saveTimer.Start();
        }
        _host.ApplyControlSettings();
    }
    private void Status(string value,bool warning=false)
    {
        StatusText.Text=value;
        StatusText.Foreground=warning?
            new SolidColorBrush(Color.FromRgb(255,194,134)):
            (Brush)Resources["TextMuted"];
    }
    private void OnPaletteChanged(ChromaticTokens _) => ApplyPalette();
    private void ApplyPalette()
    {
        var tokens=_host.DesignTokens;
        var color=tokens.Accent;
        if(_hasTokens && _lastTokens.Equals(tokens))return;
        _lastTokens=tokens;
        _hasTokens=true;
        // All WPF dynamic resources are updated together by one album event.
        // The palette is never polled by an independent SettingsWindow timer.
        Resources["AccentBrush"]=new SolidColorBrush(tokens.TextActive);
        Resources["AccentSoft"]=new SolidColorBrush(tokens.HoverSurface);
        Resources["BorderBrushCustom"]=new SolidColorBrush(tokens.GlassBorder);
        Resources["TextPrimary"]=new SolidColorBrush(tokens.TextPrimary);
        Resources["TextSecondary"]=new SolidColorBrush(tokens.TextSecondary);
        Resources["TextMuted"]=new SolidColorBrush(tokens.MicroText);
        Resources["IconBrush"]=new SolidColorBrush(tokens.Icon);
        Resources["GlassSurfaceTint"]=new SolidColorBrush(Color.FromArgb(
            _glassTintAlpha,tokens.GlassSurface.R,
            tokens.GlassSurface.G,tokens.GlassSurface.B));
        Resources["GlassHeaderSurface"]=GlassWindowMaterial.Header(tokens);
        Resources["GlassSidebarSurface"]=GlassWindowMaterial.Sidebar(tokens);
        Resources["GlassFooterSurface"]=GlassWindowMaterial.Footer(tokens);
        Resources["GlassCardSurface"]=GlassWindowMaterial.Card(tokens);
        Resources["GlassButtonSurface"]=GlassWindowMaterial.Button(tokens);
        Resources["GlassIconTile"]=GlassWindowMaterial.IconTile(tokens);
        Resources["GlassAmbientLight"]=GlassWindowMaterial.Ambient(tokens);
        Resources["GlassToggleOff"]=new SolidColorBrush(Color.FromRgb(51,72,83));
    }
    internal void NavigateTo(string section)
    {
        if(section is "Playback" or "Lyrics" or "Layout" or "Design" or "Keys" or "App")
            Select(section);
    }
    private void NavClicked(object sender,RoutedEventArgs e)
    {
        if(sender is Button b&&b.Tag is string category)Select(category);
    }
    private void Select(string category)
    {
        _recording=null;
        _section=category;
        var headings=new Dictionary<string,(string,string)>{
            ["Playback"]=("Wiedergabe","Spotify lokal steuern, ohne das Overlay zu überladen"),
            ["Lyrics"]=("Lyrics & Synchronisation","Wortgenau, verlässlich und individuell kalibrierbar"),
            ["Layout"]=("Desktop-Layout","Größe und Position der minimalistischen Anzeige"),
            ["Design"]=("Animation & Design","Albumfarben, Vinyl-Material und Visualizer"),
            ["Keys"]=("Tastenkombinationen","Gezielt registriert und jederzeit änderbar"),
            ["App"]=("Anwendung","Autostart, Tray und Diagnose")
        };
        SectionTitle.Text=headings[category].Item1;
        SectionDescription.Text=headings[category].Item2;
        foreach(var nav in new[]{NavPlayback,NavLyrics,NavLayout,NavDesign,NavKeys,NavApp}){
            if((nav.Tag as string)==category){
                nav.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty,"AccentSoft");
                nav.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty,"AccentBrush");
            }else{
                nav.Background=Brushes.Transparent;
                nav.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty,"TextPrimary");
            }
        }
        // Switching pages must never inherit the previous page's scroll target.
        _scroll.ResetToTop();
        SettingsContent.Children.Clear();
        switch(category){
            case "Playback":BuildPlayback();break;
            case "Lyrics":BuildLyrics();break;
            case "Layout":BuildLayout();break;
            case "Design":BuildDesign();break;
            case "Keys":BuildKeys();break;
            case "App":BuildApp();break;
        }
        if(!_settings.ReducedMotion){
            // Opacity only: do not animate Measure/Arrange on scroll-heavy pages.
            SettingsContent.BeginAnimation(OpacityProperty,null);
            SettingsContent.Opacity=.75;
            SettingsContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(.75,1,TimeSpan.FromMilliseconds(125)){
                    EasingFunction=new QuadraticEase {EasingMode=EasingMode.EaseOut}
                });
        }else{
            SettingsContent.BeginAnimation(OpacityProperty,null);
            SettingsContent.Opacity=1;
        }
    }
    private TextBlock Text(string value,int size=13,bool muted=false)
    {
        var block=new TextBlock{
            Text=value,FontSize=size,
            FontWeight=size>=16?FontWeights.SemiBold:FontWeights.Normal,
            TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center
        };
        block.SetResourceReference(TextBlock.ForegroundProperty,
            muted?"TextSecondary":"TextPrimary");
        return block;
    }
    private StackPanel Card(string heading,string description)
    {
        var box=new StackPanel{Margin=new Thickness(17,15,17,16)};
        box.Children.Add(Text(heading,16));
        var details=Text(description,12,true);
        details.Margin=new Thickness(0,4,0,11);
        box.Children.Add(details);
        var card=new Border {
            CornerRadius=new CornerRadius(14),
            Background=(Brush)Resources["GlassCardSurface"],
            BorderBrush=(Brush)Resources["BorderBrushCustom"],
            BorderThickness=new Thickness(1),
            Margin=new Thickness(1,0,7,13),
            Child=box
        };
        card.SetResourceReference(Border.BackgroundProperty,"GlassCardSurface");
        card.SetResourceReference(Border.BorderBrushProperty,"BorderBrushCustom");
        SettingsContent.Children.Add(card);
        return box;
    }
    private Button Button(string caption,Action execute,string? tooltip=null)
    {
        var b=new Button {Content=caption,Style=(Style)Resources["ActionButton"],
            ToolTip=tooltip};
        b.Click+=(_,_)=>{
            try{execute();}
            catch(Exception ex){SettingsStore.Log(ex);Status("Aktion fehlgeschlagen – siehe Fehlerlog.",true);}
        };
        return b;
    }
    private void Actions(StackPanel parent,params Button[] buttons)
    {
        var container=new WrapPanel{Margin=new Thickness(0,1,0,3)};
        foreach(var b in buttons)container.Children.Add(b);
        parent.Children.Add(container);
    }
    private void Toggle(StackPanel parent,string label,bool value,Action<bool> change,string? note=null)
    {
        var c=new CheckBox{Content=label,IsChecked=value,Style=(Style)Resources["AppToggle"]};
        c.Checked+=(_,_)=>{change(true);SaveSoon();};
        c.Unchecked+=(_,_)=>{change(false);SaveSoon();};
        parent.Children.Add(c);
        if(note is not null){
            var info=Text(note,11,true);
            info.Margin=new Thickness(0,-1,0,8);
            parent.Children.Add(info);
        }
    }
    private void Slider(StackPanel parent,string label,double value,double min,double max,
        double step,string units,Action<double> change)
    {
        var row=new DockPanel{Margin=new Thickness(0,8,0,2)};
        var current=Text($"{value:0.##}{units}",12,true);
        DockPanel.SetDock(current,Dock.Right);
        row.Children.Add(current);
        row.Children.Add(Text(label,13));
        parent.Children.Add(row);
        var s=new System.Windows.Controls.Slider{
            Minimum=min,Maximum=max,Value=value,TickFrequency=step,
            IsSnapToTickEnabled=true,Margin=new Thickness(0,3,8,10),
            Foreground=(Brush)Resources["AccentBrush"]
        };
        s.ValueChanged+=(_,_)=>{
            current.Text=$"{s.Value:0.##}{units}";
            change(s.Value);SaveSoon();
        };
        parent.Children.Add(s);
    }
    private void Choice(StackPanel parent,string title,
        IEnumerable<(string Display,string Value)> options,string active,Action<string> update)
    {
        var label=Text(title,13);
        label.Margin=new Thickness(0,6,0,6);
        parent.Children.Add(label);
        var buttons=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        foreach(var option in options){
            var value=option.Value;
            var b=Button(option.Display,()=>{update(value);SaveSoon();Select(_section);});
            if(value==active){
                b.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty,"AccentBrush");
                b.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty,"AccentSoft");
            }
            buttons.Children.Add(b);
        }
        parent.Children.Add(buttons);
    }
    private void BuildPlayback()
    {
        var card=Card("♫  Spotify-Steuerung","Die lokale Windows-Mediensitzung steuert Wiedergabe und Titel.");
        foreach(var actions in new []{
            new[]{VisionAction.Previous,VisionAction.PlayPause,VisionAction.Next},
            new[]{VisionAction.VolumeDown,VisionAction.Mute,VisionAction.VolumeUp}
        }){
            Actions(card,actions.Select(a=>Button(
                HotkeyManager.Label(a)+"   ·   "+HotkeyManager.DisplayBinding(_hotkeys.Binding(a)),
                ()=>_host.ExecuteAction(a))).ToArray());
        }
        var more=Card("◉  Minimales Desktop-Overlay",
            "Die Disc bleibt auf dem Desktop. Andere Anwendungen erscheinen darüber.");
        Actions(more,Button("Overlay zeigen / ausblenden",
            ()=>_host.ExecuteAction(VisionAction.ToggleOverlay)),
            Button("Einstellungen offen lassen",()=>Status("Das Einstellungsfenster bleibt unabhängig bedienbar.")));
    }
    private void BuildLyrics()
    {
        var sources=Card("≋  Automatische Lyrics","LRCLIB bevorzugt echte Wort-/Zeilenzeiten; Plaintext ist separat.");
        Toggle(sources,"Bei jedem Song automatisch suchen",_settings.AutoLyricsSearch,
            v=>_host.SetLyricsAuto(v));
        Toggle(sources,"Unsynchronisierten Text als Lesetext anzeigen",
            _settings.PlainLyricsReader,v=>_host.SetPlainReader(v),
            "Ohne diese Option bleibt die Karaoke-Fläche bei fehlenden Zeitmarken leer.");
        Actions(sources,Button("Jetzt erneut suchen",()=>_host.RescanLyrics()),
            Button("Andere Versionen finden",()=>_host.SearchLyricVersions()),
            Button("Eigene LRC/TXT importieren",()=>_host.ImportLyrics()),
            Button("Lyrics-Editor öffnen",()=>_host.OpenLyricsEditor(this)));
        var status=Card("AKTUELLE QUELLE",_host.LyricsStatus);
        Actions(status,Button("Synchronisationsdiagnose kopieren",_host.CopySyncDiagnostics));
        var sync=Card("⏱  Synchronisationskorrektur",
            "+ zeigt den Text früher an; − zeigt ihn später an. Werte bleiben gespeichert.");
        foreach(var scope in new[]{"Song","Global","Gerät"}){
            var name=scope;
            var heading=Text($"{scope}: {_host.SyncOffset(scope):+0;-0;0} ms",13);
            heading.Margin=new Thickness(0,9,0,5);sync.Children.Add(heading);
            Actions(sync,
                Button("−100 ms",()=>{_host.AdjustLyrics(scope,-100);Select("Lyrics");}),
                Button("−50 ms",()=>{_host.AdjustLyrics(scope,-50);Select("Lyrics");}),
                Button("+50 ms",()=>{_host.AdjustLyrics(scope,50);Select("Lyrics");}),
                Button("+100 ms",()=>{_host.AdjustLyrics(scope,100);Select("Lyrics");}),
                Button("Zurücksetzen",()=>{_host.ResetLyrics(scope);Select("Lyrics");}));
        }
        var karaoke=Card("✦  Wortanimation","Nur echte Wort-Zeitstempel; keine künstlichen Zeitwerte.");
        Slider(karaoke,"Lyrics-Größe",_settings.LyricsScale,.70,1.40,.05,"×",
            v=>_settings.LyricsScale=v);
        Slider(karaoke,"Glow-Intensität",_settings.LyricsGlow,0,1,.05,"",
            v=>_settings.LyricsGlow=v);
        Toggle(karaoke,"Wortvergrößerung verwenden",_settings.WordAnimationEnabled,
            v=>_settings.WordAnimationEnabled=v);
        Toggle(karaoke,"Hoher Textkontrast (Barrierefreiheit)",
            _settings.HighContrastLyrics,v=>_settings.HighContrastLyrics=v,
            "Bessere Lesbarkeit vor hellen und dunklen Wallpapers.");
        BuildLocalAlignmentSection();
    }
    private void BuildLocalAlignmentSection()
    {
        var align=Card("◉  LOKALE KI-WORTSYNCHRONISATION",
            "Nur ausdrücklich ausgewählte eigene Audiodateien + TXT/LRC. "+
            "Die Engine arbeitet einmalig offline auf der CPU, nie mit Spotify-DRM-Audio.");
        bool installed=LocalAlignmentService.TryGetConfig(out _);
        align.Children.Add(Text(installed?
            "WhisperX bereit · WAV / MP3 / FLAC / M4A · CPU-Ausführung":
            "Optionale Engine fehlt – normale Lyrics und Spotify funktionieren weiterhin.",12,true));
        var languages=new WrapPanel{Margin=new Thickness(0,9,0,7)};
        foreach(var (caption,code) in new[]{
            ("Deutsch","de"),("English","en"),("Français","fr"),
            ("Español","es"),("Italiano","it")}){
            string selected=code;
            var choice=Button(caption,()=>{_alignmentLanguage=selected;Select("Lyrics");});
            if(_alignmentLanguage==code){
                choice.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty,"AccentBrush");
                choice.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty,"AccentSoft");
            }
            languages.Children.Add(choice);
        }
        align.Children.Add(languages);
        _alignmentStatusText=Text(_alignmentStatus,12,true);
        _alignmentStatusText.Margin=new Thickness(0,7,0,9);
        align.Children.Add(_alignmentStatusText);
        var run=Button("Eigene Audiodatei + Text auswählen",()=>{
            _=RunLocalAlignmentAsync();
        },"Keine Aufnahme oder Verarbeitung von Spotify-Streams.");
        run.IsEnabled=installed&&!_alignmentBusy;
        var cancel=Button("Berechnung abbrechen",()=>_alignmentCancel?.Cancel());
        cancel.IsEnabled=_alignmentBusy;
        Actions(align,run,cancel);
        if(_alignmentPreview is not null){
            var result=_alignmentPreview;
            var review=Card("ERGEBNIS PRÜFEN — NICHT AUTOMATISCH ÜBERNEHMEN",
                $"{Path.GetFileName(result.AudioPath)} · "+ 
                $"{result.MatchedWords}/{result.SourceWords} Wörter · "+
                (double.IsFinite(result.TextSimilarity)?
                    $"Text/Audio-Prüfung {result.TextSimilarity:P0} · ":"")+
                $"{result.UncertainLines.Count} unsichere Zeilen · "+
                $"{result.Lines.Count} Zeilen. "+
                "Nicht zugeordnete Stellen enthalten keine künstlichen Wortzeiten.");
            Actions(review,
                Button("Audio + Karaoke-Vorschau öffnen",()=>OpenAlignmentPreview()),
                Button("Enhanced LRC exportieren",()=>ExportLocalAlignment()),
                Button("Für aktuellen Spotify-Titel bestätigen",()=>SaveAlignmentToCurrentSong()));
        }
    }
    private async Task RunLocalAlignmentAsync()
    {
        if(_alignmentBusy)return;
        var chooseAudio=new Microsoft.Win32.OpenFileDialog{
            Title="Eigene, rechtmäßig nutzbare Audiodatei auswählen",
            Filter="Audio (WAV, MP3, FLAC, M4A)|*.wav;*.mp3;*.flac;*.m4a",
            CheckFileExists=true,Multiselect=false
        };
        if(chooseAudio.ShowDialog(this)!=true)return;
        var chooseLyrics=new Microsoft.Win32.OpenFileDialog{
            Title="Dazugehörigen eigenen Liedtext auswählen",
            Filter="Eigene Lyrics (TXT oder LRC)|*.txt;*.lrc",
            CheckFileExists=true,Multiselect=false
        };
        if(chooseLyrics.ShowDialog(this)!=true)return;
        _alignmentBusy=true;
        _alignmentPreview=null;
        _alignmentCancel?.Dispose();
        _alignmentCancel=new CancellationTokenSource();
        _alignmentStatus="Vorbereitung der lokalen Audioausrichtung ...";
        Select("Lyrics");
        string audio=chooseAudio.FileName;
        string lyrics=chooseLyrics.FileName;
        var current=_alignmentCancel;
        var progress=new Progress<string>(value=>{
            _alignmentStatus=value;
            if(_alignmentStatusText!=null)_alignmentStatusText.Text=value;
        });
        try{
            _alignmentPreview=await LocalAlignmentService.AlignAsync(
                audio,lyrics,_alignmentLanguage,progress,current.Token);
            _alignmentStatus="Fertig: "+_alignmentPreview.MatchedWords+
                " tatsächlich zugeordnete Wörter. Ergebnis zuerst prüfen.";
        }catch(OperationCanceledException){
            _alignmentStatus="Lokale Ausrichtung abgebrochen.";
        }catch(Exception ex){
            _alignmentStatus="Lokale Ausrichtung fehlgeschlagen: "+ex.Message;
            SettingsStore.Log(ex);
        }finally{
            _alignmentBusy=false;
            if(ReferenceEquals(_alignmentCancel,current))_alignmentCancel=null;
            current.Dispose();
            if(IsLoaded&&IsVisible)Select("Lyrics");
        }
    }
    private void OpenAlignmentPreview()
    {
        if(_alignmentPreview is null)return;
        var preview=new AlignmentPreviewWindow(_alignmentPreview,_host.ActivePalette){
            Owner=this
        };
        preview.Show();
    }
    private void ExportLocalAlignment()
    {
        if(_alignmentPreview is null)return;
        var dialog=new Microsoft.Win32.SaveFileDialog{
            Title="Bestätigte lokale Lyrics exportieren",
            FileName=Path.GetFileNameWithoutExtension(_alignmentPreview.AudioPath)+".lrc",
            Filter="Enhanced LRC (Wortzeiten)|*.lrc|Lyricsfile YAML|*.yaml",
            AddExtension=true
        };
        if(dialog.ShowDialog(this)!=true)return;
        string data=Path.GetExtension(dialog.FileName).Equals(".yaml",
            StringComparison.OrdinalIgnoreCase)?
            _alignmentPreview.ToLyricsfile():_alignmentPreview.ToEnhancedLrc();
        File.WriteAllText(dialog.FileName,data,
            new System.Text.UTF8Encoding(false));
        Status("Lokale Wort-Zeitstempel exportiert. Die Audio-Datei bleibt unverändert.");
    }
    private void SaveAlignmentToCurrentSong()
    {
        if(_alignmentPreview is null)return;
        string track=_host.CurrentTrackForAlignment;
        if(string.IsNullOrWhiteSpace(track)){
            Status("Kein gültiger Spotify-Song für die Zuordnung erkannt.",true);
            return;
        }
        if(MessageBox.Show(this,
            "Sind die gewählte eigene Aufnahme und der eigene Text wirklich "+
            "dieselbe Songversion wie der aktuell erkannte Titel?\n\n"+
            track+"\n\nNur bei Übereinstimmung speichern.",
            "Wortzeiten zu Spotify-Titel zuordnen",
            MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
            return;
        var yaml=_alignmentPreview.ToLyricsfile();
        if(_host.ImportAlignedLyrics(yaml))
            Status("Lokale Wortzeitstempel diesem Song zugeordnet; Karaoke wird aktualisiert.");
        else Status("Import nicht möglich. Bitte erst einen Song abspielen.",true);
    }
    private void BuildLayout()
    {
        var size=Card("▦  Größe und Abstände","Änderungen gelten sofort für das Desktop-Overlay.");
        Slider(size,"Gesamte Größe",_settings.Scale,.65,1.65,.05,"×",v=>_settings.Scale=v);
        Slider(size,"Vinyl-Größe",_settings.VinylScale,.70,1.15,.05,"×",
            v=>_settings.VinylScale=v);
        Slider(size,"Abstand zu Lyrics",_settings.LyricsSpacing,0,70,5," px",
            v=>_settings.LyricsSpacing=v);
        var position=Card("⌖  Desktop und Monitore",
            "Das Overlay bleibt vor Wallpaper Engine, aber hinter normalen Fenstern.");
        Toggle(position,"Position sperren",_settings.PositionLocked,v=>_settings.PositionLocked=v);
        Toggle(position,"Klickdurchlässig (Steuerung über Tray/Hotkeys)",
            _settings.ClickThrough,v=>_host.SetClickThrough(v));
        Actions(position,Button("Position auf dem Desktop verschieben",()=>{
            _host.PrepareMove();Status("Auf die Disc klicken und ziehen. Einstellungen können geschlossen werden.");
        }));
        int index=0;
        foreach(var screen in WinForms.Screen.AllScreens){
            int selected=index++;
            Actions(position,Button($"Monitor {selected+1} · {screen.Bounds.Width}×{screen.Bounds.Height}"+
                (screen.Primary?" (Hauptmonitor)":""),
                ()=>{_host.MoveToMonitor(selected);Status("Overlay auf Monitor "+(selected+1)+" verschoben.");}));
        }
    }
    private void BuildDesign()
    {
        var colors=Card("◈  Adaptive Albumfarben",
            "Menü, Schalter, Glasränder, Vinyl und Lyrics folgen dem Albumcover.");
        Toggle(colors,"Automatische Albumfarben aktivieren",
            _settings.AlbumColorsEnabled,v=>_settings.AlbumColorsEnabled=v,
            "Ohne Musik wird eine neutrale dunkel-silberne Palette verwendet.");
        var vinyl=Card("◎  Vinyl-Material","Feine Rillen und hochwertige Reflexionen aus dem vorhandenen Renderer.");
        Choice(vinyl,"Qualitätsprofil",
            new[]{("Ultra Light","UltraLight"),("Balanced","Balanced"),("Cinematic","Cinematic")},
            _settings.VinylQuality,v=>_settings.VinylQuality=v);
        Toggle(vinyl,"Vinyl-Disc drehen",_settings.VinylRotationEnabled,
            v=>_settings.VinylRotationEnabled=v);
        Slider(vinyl,"Grundgeschwindigkeit",_settings.VinylBaseRpm,24,52,.5," RPM",
            v=>_settings.VinylBaseRpm=v);
        Toggle(vinyl,"Musikadaptive Drehzahl (bei zuverlässigem Takt)",
            _settings.AdaptiveVinylTempoEnabled,v=>_settings.AdaptiveVinylTempoEnabled=v,
            "Echte lokale Audioimpulse, kein erfundener BPM-Wert; sonst feste Grundgeschwindigkeit.");
        Toggle(vinyl,"Albumcover dreht mit",_settings.CoverRotates,v=>_settings.CoverRotates=v);
        Toggle(vinyl,"Mittelloch: echter transparenter Desktop-Hintergrund",
            _settings.VinylCenterTransparent,v=>_settings.VinylCenterTransparent=v,
            "Aus = dunkler Vinyl-Mittelpunkt. Das Albumcover bleibt erhalten.");
        var frequency=Card("≋  Audio-Visualizer","Echte WASAPI-Frequenzdaten mit begrenzter Verstärkung.");
        Slider(frequency,"Empfindlichkeit",_settings.AudioSensitivity,.5,2.5,.1,"×",
            v=>_settings.AudioSensitivity=v);
        Slider(frequency,"Glättung",_settings.AudioSmoothing,.5,2.5,.1,"×",
            v=>_settings.AudioSmoothing=v);
        Choice(frequency,"Animationsprofil",
            new[]{("Ultra Light","Ultra Light"),("Balanced","Balanced"),
                ("Smooth","Smooth"),("High Refresh","High Refresh"),
                ("Cinematic","Cinematic")},
            _settings.RenderProfile,v=>{
                _settings.RenderProfile=v;
                if(v=="Cinematic")_settings.VinylQuality="Cinematic";
            });
        Toggle(frequency,"Bewegungen reduzieren",_settings.ReducedMotion,
            v=>_settings.ReducedMotion=v);
    }
    private void BuildKeys()
    {
        var info=Card("⌨  SICHERE HOTKEYS",
            "Nur konfigurierte Kombinationen. Keine Tastatureingaben werden aufgezeichnet oder protokolliert.");
        Toggle(info,"Globale Tastenkombinationen aktivieren",_settings.GlobalHotkeysEnabled,
            v=>{
                _hotkeys.SetGlobal(v,out string message);
                Status(message,!message.StartsWith("Aktiv")&&v);
            },"Standardmäßig AUS. Space ist nur bei fokussierter Bedienoberfläche aktiv.");
        info.Children.Add(Text(_hotkeys.Status,12,true));
        Actions(info,Button("Standardbelegung wiederherstellen",()=>{
            _hotkeys.Reset();Status("Standardbelegung wiederhergestellt.");Select("Keys");
        }));
        var mapping=Card("DEINE TASTENKOMBINATIONEN",
            "Ändern → Kombination drücken → prüfen → speichern. Escape bricht ab.");
        foreach(var action in HotkeyManager.Actions){
            var selected=action;
            // Two responsive lines: no fixed 165+95+85 px columns that clip
            // keybind editing buttons on a smaller monitor or high DPI.
            var row=new StackPanel{Margin=new Thickness(0,4,0,9)};
            var caption=new WrapPanel{Margin=new Thickness(0,0,0,3)};
            var name=Text(HotkeyManager.Label(action),13);
            name.Margin=new Thickness(0,0,13,0);
            caption.Children.Add(name);
            var current=Text(HotkeyManager.DisplayBinding(_hotkeys.Binding(action)),12,true);
            caption.Children.Add(current);
            row.Children.Add(caption);
            var change=Button("Ändern",()=>{
                _recording=selected;
                Status($"Drücke jetzt die neue Kombination für {HotkeyManager.Label(selected)}. Escape = Abbrechen.");
                Keyboard.Focus(this);
            });
            var remove=Button("Entfernen",()=>{
                if(_hotkeys.Configure(selected,"",out var msg)){
                    Status(msg);Select("Keys");
                }else Status(msg,true);
            });
            var controls=new WrapPanel();
            change.Margin=new Thickness(0,0,7,3);
            remove.Margin=new Thickness(0,0,0,3);
            controls.Children.Add(change);controls.Children.Add(remove);
            row.Children.Add(controls);
            mapping.Children.Add(row);
        }
    }
    private void BuildApp()
    {
        var settings=Card("⚙  Anwendung","Einstellungen werden lokal und atomar im Benutzerprofil gespeichert.");
        Toggle(settings,"Mit Windows starten",_settings.AutoStart,
            v=>_host.SetAutoStart(v));
        var update=Card("CHARLI VISION — UPDATE CENTER",
            "Kostenlose GitHub-Updates. Signatur und SHA-256 werden vor der Installation geprüft.");
        Toggle(update,"Automatisch nach Updates suchen",
            _settings.AutomaticUpdateChecks,
            v=>_settings.AutomaticUpdateChecks=v,
            "Es wird niemals ohne deine Zustimmung installiert.");
        var current=Text("Installiert: "+VisionUpdateService.Shared.InstalledVersion,12,true);
        var latest=Text("Verfügbar: "+VisionUpdateService.Shared.AvailableVersion,12,true);
        var state=Text(VisionUpdateService.Shared.Status,12,true);
        var notes=Text(VisionUpdateService.Shared.ReleaseNotes,11,true);
        var progress=new ProgressBar{Height=7,Minimum=0,Maximum=100,Value=0,
            Margin=new Thickness(0,9,0,7)};
        progress.SetResourceReference(ProgressBar.ForegroundProperty,"AccentBrush");
        var progressLabel=Text("",11,true);
        update.Children.Add(current);update.Children.Add(latest);
        update.Children.Add(state);update.Children.Add(notes);
        update.Children.Add(progress);update.Children.Add(progressLabel);
        void RefreshUpdates(){
            latest.Text="Verfügbar: "+VisionUpdateService.Shared.AvailableVersion;
            state.Text=VisionUpdateService.Shared.Status;
            notes.Text=VisionUpdateService.Shared.ReleaseNotes;
        }
        bool updateBusy=false;
        var searchUpdate=Button("Nur nach Updates suchen",async()=>{
            if(updateBusy)return;
            updateBusy=true;
            try{
                state.Text="Prüfe signiertes GitHub-Release …";
                await VisionUpdateService.Shared.CheckAsync(force:true);
                RefreshUpdates();
            }catch(OperationCanceledException){
                state.Text="Updateprüfung abgebrochen.";
            }catch(Exception ex){
                SettingsStore.Log(ex);state.Text="Updateprüfung fehlgeschlagen.";
            }finally{updateBusy=false;}
        });
        var installUpdate=Button("Update suchen und installieren",async()=>{
            if(updateBusy)return;
            updateBusy=true;
            searchUpdate.IsEnabled=false;
            try{
                // ONE explicit button click performs both discovery and download.
                // OS consent and the Inno Setup wizard are intentionally respected.
                if(!VisionUpdateService.Shared.CanInstall){
                    state.Text="Prüfe signiertes GitHub-Release …";
                    await VisionUpdateService.Shared.CheckAsync(force:true);
                }
                RefreshUpdates();
                if(!VisionUpdateService.Shared.CanInstall)return;
                state.Text="Signierten Installer laden und prüfen …";
                var report=new Progress<(long received,long total)>(value=>{
                    if(value.total>0)
                        progress.Value=Math.Clamp(value.received*100d/value.total,0,100);
                    progressLabel.Text=$"{value.received/1048576d:F1} / "+
                        $"{value.total/1048576d:F1} MB";
                });
                bool started=await VisionUpdateService.Shared.DownloadAndStartInstallerAsync(report);
                RefreshUpdates();
                if(started)_host.ExitFromSettings();
            }catch(OperationCanceledException){
                state.Text="Update abgebrochen.";
            }catch(Exception ex){
                SettingsStore.Log(ex);state.Text="Update fehlgeschlagen: "+ex.Message;
            }finally{
                updateBusy=false;
                searchUpdate.IsEnabled=true;
            }
        });
        Actions(update,searchUpdate,installUpdate);
        var status=Card("DIAGNOSE & HILFE",
            "Charli Vision benötigt keine kostenpflichtigen API-Keys.");
        status.Children.Add(Text(_host.GetLiveDiagnostics(),12,true));
        Actions(status,Button("Live-Diagnose aktualisieren",()=>Select("App")),
            Button("Diagnose kopieren",()=>{
                try{Clipboard.SetText(_host.GetLiveDiagnostics());
                    Status("Diagnose kopiert (ohne persönliche Audioinhalte).");
                }catch(Exception ex){Status(ex.Message,true);}
            }),
            Button("Sync-Diagnose kopieren",_host.CopySyncDiagnostics),
            Button("Dokumentationsordner öffnen",()=>{
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{
                    FileName=SettingsStore.DirectoryPath,UseShellExecute=true});
            }));
        var commands=Card("ANWENDUNGSSTEUERUNG",
            "Das Einstellungsfenster ist eine normale Anwendung – das Musik-Overlay bleibt Desktop-only.");
        Actions(commands,Button("Overlay anzeigen",()=>_host.ShowOverlayFromSettings()),
            Button("Overlay ausblenden",()=>_host.HideOverlayFromSettings()),
            Button("Charli Vision beenden",()=>_host.ExitFromSettings()));
    }
    private void KeyPreview(object sender,KeyEventArgs e)
    {
        if(_recording is VisionAction recording){
            e.Handled=true;
            Key actual=e.Key==Key.System?e.SystemKey:e.Key;
            if(actual==Key.Escape){
                _recording=null;Status("Tastenaufnahme abgebrochen.");return;
            }
            string? candidate=HotkeyManager.FromKey(actual,Keyboard.Modifiers);
            if(candidate is null)return;
            if(_hotkeys.Configure(recording,candidate,out var message)){
                _recording=null;
                Select("Keys");Status($"Gespeichert: {HotkeyManager.Label(recording)} – {candidate}");
            }else Status(message,true);
            return;
        }
        if(e.OriginalSource is TextBoxBase||e.OriginalSource is ButtonBase||
            e.OriginalSource is Slider||e.OriginalSource is ComboBox)return;
        Key key=e.Key==Key.System?e.SystemKey:e.Key;
        var input=HotkeyManager.FromKey(key,Keyboard.Modifiers);
        if(input!=null&&HotkeyManager.Actions.Any(a=>
            _hotkeys.Binding(a).Equals(input,StringComparison.OrdinalIgnoreCase))){
            _hotkeys.ProcessFocusedKey(key,Keyboard.Modifiers);
            e.Handled=true;
        }
    }
    private void HeaderDown(object sender,MouseButtonEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed)return;
        try {if(e.ClickCount==2)WindowState=WindowState==WindowState.Maximized?
                WindowState.Normal:WindowState.Maximized;
             else DragMove();}
        catch(Exception ex){SettingsStore.Log(ex);}
    }
    private void CloseClick(object sender,RoutedEventArgs e)=>Close();
    private void MinimizeClick(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
}
