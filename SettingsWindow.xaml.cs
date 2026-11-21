using System;
using System.Collections.Generic;
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
using System.Windows.Threading;
using WinForms=System.Windows.Forms;

namespace CharliVision;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _host;
    private readonly AppSettings _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _paletteTimer=new(){Interval=TimeSpan.FromMilliseconds(60)};
    private readonly DispatcherTimer _saveTimer=new(){Interval=TimeSpan.FromMilliseconds(330)};
    private VisionAction? _recording;
    private CancellationTokenSource? _alignmentCancel;
    private LocalAlignmentService.Preview? _alignmentPreview;
    private string _alignmentStatus="Bereit – nur eigene, lokal ausgewählte Dateien";
    private string _alignmentLanguage="en";
    private TextBlock? _alignmentStatusText;
    private bool _alignmentBusy;
    private string _section="Playback";
    private Color _lastAccent=Colors.Transparent;
    public SettingsWindow(MainWindow host,AppSettings settings,HotkeyManager hotkeys)
    {
        InitializeComponent();
        _host=host;_settings=settings;_hotkeys=hotkeys;
        _paletteTimer.Tick+=(_,_)=>ApplyPalette();
        _saveTimer.Tick+=(_,_)=>{_saveTimer.Stop();SettingsStore.Save(_settings);};
        Loaded+=(_,_)=>{
            _paletteTimer.Start();ApplyPalette();Select("Playback");Keyboard.Focus(this);
        };
        Closed+=(_,_)=>{
            _alignmentCancel?.Cancel();
            _paletteTimer.Stop();_saveTimer.Stop();SettingsStore.Save(_settings);
        };
        PreviewKeyDown+=KeyPreview;
    }
    private void SaveSoon()
    {
        _saveTimer.Stop();_saveTimer.Start();
        _host.ApplyControlSettings();
    }
    private void Status(string value,bool warning=false)
    {
        StatusText.Text=value;
        StatusText.Foreground=warning?
            new SolidColorBrush(Color.FromRgb(255,194,134)):
            (Brush)Resources["TextMuted"];
    }
    private void ApplyPalette()
    {
        var palette=_settings.AlbumColorsEnabled?_host.ActivePalette:AlbumPalette.Neutral;
        var color=palette.Accent;
        if(Math.Abs(color.R-_lastAccent.R)+Math.Abs(color.G-_lastAccent.G)+
           Math.Abs(color.B-_lastAccent.B)<4)return;
        _lastAccent=color;
        // WPF is allowed to freeze shared resource brushes. Replacing the resource
        // updates DynamicResource references safely without mutating a frozen object.
        Resources["AccentBrush"]=new SolidColorBrush(color);
        Resources["AccentSoft"]=new SolidColorBrush(
            Color.FromArgb(42,color.R,color.G,color.B));
        Resources["BorderBrushCustom"]=new SolidColorBrush(
            AlbumPalette.Mix(Color.FromArgb(65,55,75,91),
                Color.FromArgb(132,color.R,color.G,color.B),.60));
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
            if((nav.Tag as string)==category)
                nav.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty,"AccentSoft");
            else nav.Background=Brushes.Transparent;
        }
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
            SettingsContent.Opacity=.35;
            SettingsContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(.35,1,TimeSpan.FromMilliseconds(160)){
                    EasingFunction=new QuadraticEase {EasingMode=EasingMode.EaseOut}
                });
        }else{
            SettingsContent.BeginAnimation(OpacityProperty,null);
            SettingsContent.Opacity=1;
        }
    }
    private static TextBlock Text(string value,int size=13,bool muted=false)
    {
        return new TextBlock{
            Text=value,FontSize=size,
            FontWeight=size>=16?FontWeights.SemiBold:FontWeights.Normal,
            Foreground=new SolidColorBrush(muted?Color.FromRgb(173,190,205):
                Color.FromRgb(238,246,252)),
            TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center
        };
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
            Background=new SolidColorBrush(Color.FromArgb(238,25,37,48)),
            BorderBrush=(Brush)Resources["BorderBrushCustom"],
            BorderThickness=new Thickness(1),
            Margin=new Thickness(1,0,7,13),
            Child=box
        };
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
            var row=new Grid{Margin=new Thickness(0,4,0,6)};
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(165)});
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(95)});
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(85)});
            var name=Text(HotkeyManager.Label(action),13);name.Margin=new Thickness(0,0,6,0);
            row.Children.Add(name);
            var current=Text(HotkeyManager.DisplayBinding(_hotkeys.Binding(action)),12,true);
            Grid.SetColumn(current,1);row.Children.Add(current);
            var change=Button("Ändern",()=>{
                _recording=selected;
                Status($"Drücke jetzt die neue Kombination für {HotkeyManager.Label(selected)}. Escape = Abbrechen.");
                Keyboard.Focus(this);
            });
            change.Margin=new Thickness(2);Grid.SetColumn(change,2);row.Children.Add(change);
            var remove=Button("Entfernen",()=>{
                if(_hotkeys.Configure(selected,"",out var msg)){
                    Status(msg);Select("Keys");
                }else Status(msg,true);
            });
            remove.Margin=new Thickness(2);Grid.SetColumn(remove,3);row.Children.Add(remove);
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
        Actions(update,
            Button("Nach Updates suchen",async()=>{
                state.Text="Prüfe GitHub-Release …";
                await VisionUpdateService.Shared.CheckAsync(force:true);
                RefreshUpdates();
            }),
            Button("Jetzt aktualisieren",async()=>{
                if(!VisionUpdateService.Shared.CanInstall){
                    RefreshUpdates();return;
                }
                state.Text="Prüfe und lade signierten Installer …";
                var report=new Progress<(long received,long total)>(value=>{
                    if(value.total>0)
                        progress.Value=Math.Clamp(value.received*100d/value.total,0,100);
                    progressLabel.Text=$"{value.received/1048576d:F1} / "+
                        $"{value.total/1048576d:F1} MB";
                });
                bool started=await VisionUpdateService.Shared.DownloadAndStartInstallerAsync(report);
                RefreshUpdates();
                if(started)_host.ExitFromSettings();
            }));
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
