using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms=System.Windows.Forms;
using MediaColor=System.Windows.Media.Color;
using MediaBrush=System.Windows.Media.Brush;
using MediaPoint=System.Windows.Point;

namespace CharliVision;

// One floating, context-sized menu; the full SettingsWindow is now optional detail UI.
public partial class GlassMenuWindow : Window
{
    private readonly MainWindow _host;
    private readonly AppSettings _settings;
    private readonly HotkeyManager _keys;
    private readonly SmoothScrollController _scroll;
    private ChromaticTokens _lastTokens;
    private bool _hasTokens;
    private byte _backdropTintAlpha=89;
    private string _syncScope="Song";
    private bool _closing;
    private bool _building;
    public GlassMenuWindow(MainWindow host,AppSettings settings,
        HotkeyManager hotkeys,MediaPoint screenAt)
    {
        InitializeComponent();
        _host=host;_settings=settings;_keys=hotkeys;
        _scroll=new SmoothScrollController(MenuScroller,()=>_settings.ReducedMotion);
        var screen=WinForms.Screen.FromPoint(new System.Drawing.Point(
            (int)screenAt.X,(int)screenAt.Y));
        var area=screen.WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(host);
        double scaleX=Math.Max(.5,dpi.DpiScaleX),scaleY=Math.Max(.5,dpi.DpiScaleY);
        double left=screenAt.X/scaleX-Width*.34;
        double top=screenAt.Y/scaleY-Height*.43;
        Left=Math.Clamp(left,area.Left/scaleX+5,
            Math.Max(area.Left/scaleX+5,area.Right/scaleX-Width-5));
        Top=Math.Clamp(top,area.Top/scaleY+5,
            Math.Max(area.Top/scaleY+5,area.Bottom/scaleY-Height-5));
        // Always fit the actual monitor work area, even at smaller resolutions.
        MaxHeight=Math.Min(786,Math.Max(260,area.Height/scaleY-16));
        Height=MaxHeight;
        Flyout.CustomPopupPlacementCallback=PlaceFlyout;
        OuterShell.SizeChanged+=(_,e)=>UpdateCornerClip(e.NewSize);
        SourceInitialized+=(_,_)=>{
            GlassWindowMaterial.Apply(this,transient:true);
            CaptureBlurredBackdrop();
        };
        _host.ChromaticPaletteChanged+=OnChromaticPaletteChanged;
        Loaded+=(_,_)=>{Rebuild();UpdateColors(true);};
        Closing+=(_,_)=>{_closing=true;Flyout.IsOpen=false;};
        Closed+=(_,_)=>{
            _closing=true;
            _host.ChromaticPaletteChanged-=OnChromaticPaletteChanged;
            _scroll.Dispose();
            Flyout.IsOpen=false;
        };
        Deactivated+=(_,_)=>{
            // WPF can raise Deactivated while Close() is already in progress.
            // Defer closing and recheck the state to avoid reentrant Window.Close.
            if(_closing || Flyout.IsOpen || !IsLoaded)return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                if(!_closing && IsLoaded && !IsActive && !Flyout.IsOpen)
                    RequestClose();
            }));
        };
        PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){RequestClose();e.Handled=true;}
            else if(e.Key==Key.Space && e.OriginalSource is not Button){
                _host.ExecuteAction(VisionAction.PlayPause);e.Handled=true;
            }
        };
    }
    public void ActivateMenu()
    {
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Show();Activate();
    }
    private void OnChromaticPaletteChanged(ChromaticTokens _) => UpdateColors();
    private void UpdateColors(bool force=false)
    {
        var tokens=_host.DesignTokens;
        MediaColor accent=tokens.Accent;
        if(!force && _hasTokens && _lastTokens.Equals(tokens))return;
        _lastTokens=tokens;
        _hasTokens=true;
        Resources["GlassAccent"]=new SolidColorBrush(tokens.TextActive);
        Resources["GlassAccentSoft"]=new SolidColorBrush(tokens.HoverSurface);
        Resources["GlassLine"]=new SolidColorBrush(tokens.GlassBorder);
        Resources["GlassIcon"]=new SolidColorBrush(tokens.Icon);
        Resources["GlassIconSoft"]=new SolidColorBrush(tokens.HoverSurface);
        Resources["GlassTextPrimary"]=new SolidColorBrush(tokens.TextPrimary);
        Resources["GlassTextSecondary"]=new SolidColorBrush(tokens.TextSecondary);
        Resources["GlassTintBrush"]=new SolidColorBrush(tokens.GlassSurface);
        GlassTint.Background=new SolidColorBrush(MediaColor.FromArgb(
            _backdropTintAlpha,tokens.GlassSurface.R,
            tokens.GlassSurface.G,tokens.GlassSurface.B));
        if(OuterShell.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
            glow.Color=accent;
        if(FlyoutShell.Effect is System.Windows.Media.Effects.DropShadowEffect popupGlow)
            popupGlow.Color=accent;
    }
    internal void UpdateCornerClip(System.Windows.Size size)
    {
        // WPF Border.CornerRadius does not automatically clip its children.
        if(size.Width<=0||size.Height<=0)return;
        var rounded=new RectangleGeometry(new Rect(0,0,
            size.Width,size.Height),27,27);
        rounded.Freeze();
        GlassContentGrid.Clip=rounded;
    }
    private void CaptureBlurredBackdrop()
    {
        // Single small in-memory capture for a genuine frosted-glass appearance.
        // This is never written to disk, does not poll the screen, and stops when closed.
        try{
            var dpi=VisualTreeHelper.GetDpi(this);
            int w=(int)Math.Ceiling(Width*dpi.DpiScaleX);
            int h=(int)Math.Ceiling(Height*dpi.DpiScaleY);
            if(w>1400 || h>2000 || w<200 || h<300)return;
            MediaPoint p=PointToScreen(new MediaPoint(0,0));
            using var bmp=new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var graphics=Graphics.FromImage(bmp)){
                graphics.CopyFromScreen((int)p.X,(int)p.Y,0,0,
                    new System.Drawing.Size(w,h),CopyPixelOperation.SourceCopy);
            }
            // Preserve readable text on bright wallpapers while leaving dark
            // backgrounds appreciably more visible through the glass.
            double luminance=0;int samples=0;
            for(int y=1;y<=3;y++)for(int x=1;x<=3;x++){
                var px=bmp.GetPixel(w*x/4,h*y/4);
                luminance+=.2126*px.R+.7152*px.G+.0722*px.B;
                samples++;
            }
            _backdropTintAlpha=(byte)(luminance/samples>145?138:86);
            UpdateColors(true);
            IntPtr handle=bmp.GetHbitmap();
            try{
                var source=Imaging.CreateBitmapSourceFromHBitmap(handle,IntPtr.Zero,
                    Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                Backdrop.Source=source;
            }finally{DeleteObject(handle);}
        }catch(Exception){Backdrop.Source=null;}
    }
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr obj);
    private CustomPopupPlacement[] PlaceFlyout(System.Windows.Size popup,
        System.Windows.Size target,MediaPoint offset)
    {
        var targetPoint=Flyout.PlacementTarget as FrameworkElement;
        if(targetPoint is null)return new[]{
            new CustomPopupPlacement(new MediaPoint(target.Width+3,0),PopupPrimaryAxis.Horizontal)};
        var current=targetPoint.PointToScreen(new MediaPoint(0,0));
        var screen=WinForms.Screen.FromPoint(
            new System.Drawing.Point((int)current.X,(int)current.Y));
        var dpi=VisualTreeHelper.GetDpi(targetPoint);
        double remaining=(screen.WorkingArea.Right-current.X)/dpi.DpiScaleX;
        double x=remaining>popup.Width+target.Width+10?
            target.Width+5:-(popup.Width+5);
        double upper=(screen.WorkingArea.Top-current.Y)/dpi.DpiScaleY+9;
        double lower=(screen.WorkingArea.Bottom-current.Y)/dpi.DpiScaleY-popup.Height-9;
        double y=Math.Clamp(-(popup.Height-target.Height)/2,
            Math.Min(upper,lower),Math.Max(upper,lower));
        return new[]{new CustomPopupPlacement(
            new MediaPoint(x,y),
            PopupPrimaryAxis.Horizontal)};
    }
    private TextBlock Text(string content,int size=13,bool bold=false)
    {
        var block=new TextBlock{
            Text=content,FontSize=size,FontFamily=new System.Windows.Media.FontFamily("Segoe UI Variable Text,Segoe UI"),
            FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,
            VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis
        };
        block.SetResourceReference(TextBlock.ForegroundProperty,
            size<=11?"GlassTextSecondary":"GlassTextPrimary");
        return block;
    }
    private void Section(string icon,string label)
    {
        var grid=new Grid{Margin=new Thickness(3,3,0,1),Height=19};
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var glyph=Text(icon,13,true);glyph.Foreground=(MediaBrush)Resources["GlassAccent"];
        glyph.SetResourceReference(TextBlock.ForegroundProperty,"GlassAccent");
        glyph.Margin=new Thickness(0,0,12,0);
        grid.Children.Add(glyph);
        var title=Text(label.ToUpperInvariant(),11,true);
        title.SetResourceReference(TextBlock.ForegroundProperty,"GlassAccent");
        Grid.SetColumn(title,1);grid.Children.Add(title);
        var line=new Border{Height=1,Margin=new Thickness(12,0,0,0),
            VerticalAlignment=VerticalAlignment.Center};
        line.SetResourceReference(Border.BackgroundProperty,"GlassLine");
        Grid.SetColumn(line,2);grid.Children.Add(line);
        MenuItems.Children.Add(grid);
    }
    private Button Row(string glyph,string caption,string trailing,Action click,
        bool? on=null,string? hint=null)
    {
        var button=new Button{
            Style=(Style)Resources["MenuRow"],ToolTip=hint};
        var grid=new Grid{VerticalAlignment=VerticalAlignment.Center};
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(36)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var iconShell=new Border {
            Width=27,Height=23,CornerRadius=new CornerRadius(7)
        };
        iconShell.SetResourceReference(Border.BackgroundProperty,"GlassIconSoft");
        var iconText=new TextBlock{
            Text=glyph,FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets,Segoe UI Symbol"),
            FontSize=16,TextAlignment=TextAlignment.Center,
            VerticalAlignment=VerticalAlignment.Center
        };
        iconText.SetResourceReference(TextBlock.ForegroundProperty,"GlassIcon");
        iconShell.Child=iconText;
        grid.Children.Add(iconShell);
        var title=Text(caption,13);
        title.Margin=new Thickness(4,0,7,0);Grid.SetColumn(title,1);
        grid.Children.Add(title);
        if(on.HasValue){
            var pill=new Border{Width=38,Height=20,CornerRadius=new CornerRadius(10),
                Background=on.Value?(MediaBrush)Resources["GlassAccent"]:
                    new SolidColorBrush(MediaColor.FromRgb(62,81,84)),
                Margin=new Thickness(2,0,4,0)};
            if(on.Value)pill.SetResourceReference(Border.BackgroundProperty,"GlassAccent");
            pill.Child=new Ellipse {Width=15,Height=15,
                Fill=System.Windows.Media.Brushes.White,Margin=new Thickness(3),
                HorizontalAlignment=on.Value?HorizontalAlignment.Right:HorizontalAlignment.Left};
            Grid.SetColumn(pill,2);grid.Children.Add(pill);
        }else if(trailing.Length>0){
            var right=new Border {
                CornerRadius=new CornerRadius(10),
                Background=new SolidColorBrush(MediaColor.FromArgb(75,30,58,61)),
                Padding=new Thickness(7,3,7,3),
                Margin=new Thickness(3,0,3,0)
            };
            right.Child=Text(trailing,10);
            Grid.SetColumn(right,2);grid.Children.Add(right);
        }
        button.Content=grid;
        button.Click+=(_,_)=>{
            try{click();}
            catch(Exception ex){SettingsStore.Log(ex);}
        };
        MenuItems.Children.Add(button);
        return button;
    }
    private void Toggle(string glyph,string label,bool selected,Action change,string? tooltip=null)
    {
        Row(glyph,label,"",()=>{
            change();ApplyAndSave();Rebuild();UpdateColors(true);
        },selected,tooltip);
    }
    private void Act(string glyph,string label,string right,Action action,bool close=true)
    {
        Row(glyph,label,right,()=>{
            if(close)RequestClose();
            action();
        });
    }
    private void Sub(string glyph,string label,string right,Action<StackPanel> contents)
    {
        Button? b=null;
        b=Row(glyph,label,right,()=>{
            if(b is null)return;
            Flyout.IsOpen=false;
            Flyout.PlacementTarget=b;
            FlyoutItems.Children.Clear();
            contents(FlyoutItems);
            Flyout.IsOpen=true;
        });
    }
    private void FlyoutHeader(StackPanel panel,string heading,string description)
    {
        var label=Text(heading,13,true);
        label.SetResourceReference(TextBlock.ForegroundProperty,"GlassAccent");
        label.Margin=new Thickness(5,3,4,3);panel.Children.Add(label);
        var hint=Text(description,10);
        hint.Opacity=.73;hint.TextWrapping=TextWrapping.Wrap;
        hint.Margin=new Thickness(5,0,4,8);panel.Children.Add(hint);
    }
    private void FlyoutAction(StackPanel panel,string caption,string right,Action doIt)
    {
        var btn=new Button{Style=(Style)Resources["MenuRow"],
            Height=36,Margin=new Thickness(1,2,1,2)};
        var root=new DockPanel{LastChildFill=true};
        var meta=Text(right,11);
        meta.Opacity=.82;
        DockPanel.SetDock(meta,Dock.Right);
        root.Children.Add(meta);
        var title=Text(caption,12,true);
        title.Margin=new Thickness(8,0,4,0);
        root.Children.Add(title);
        btn.Content=root;
        btn.Click+=(_,_)=>{
            try{doIt();}
            catch(Exception ex){SettingsStore.Log(ex);}
        };
        panel.Children.Add(btn);
    }
    private void FlyoutInfo(StackPanel panel,string title,string detail)
    {
        var row=new DockPanel{Margin=new Thickness(8,5,8,5)};
        var right=Text(detail,10);right.Opacity=.75;
        DockPanel.SetDock(right,Dock.Right);row.Children.Add(right);
        row.Children.Add(Text(title,12));panel.Children.Add(row);
    }
    private void SyncFlyout(StackPanel panel)
    {
        FlyoutHeader(panel,"LYRICS · SYNCHRONISATION",
            "Echte Zeitstempel · sofortige Korrektur");
        foreach(var scope in new[]{"Song","Global","Gerät"}){
            var which=scope;
            FlyoutAction(panel,(_syncScope==scope?"● ":"○ ")+
                (scope=="Song"?"Dieser Song":scope=="Gerät"?"Audiogerät":"Global"),
                $"{_host.SyncOffset(scope):+0;-0;0} ms",()=>{
                    _syncScope=which;FlyoutItems.Children.Clear();SyncFlyout(FlyoutItems);
                });
        }
        var offset=_host.SyncOffset(_syncScope);
        FlyoutAction(panel,"◷  Früher (+50 ms)","",()=>{
            _host.AdjustLyrics(_syncScope,50);
            FlyoutItems.Children.Clear();SyncFlyout(FlyoutItems);
        });
        FlyoutAction(panel,"◷  Später (−50 ms)","",()=>{
            _host.AdjustLyrics(_syncScope,-50);
            FlyoutItems.Children.Clear();SyncFlyout(FlyoutItems);
        });
        FlyoutAction(panel,"↺  Zurücksetzen","",()=>{
            _host.ResetLyrics(_syncScope);
            FlyoutItems.Children.Clear();SyncFlyout(FlyoutItems);
        });
        FlyoutAction(panel,"Sync-Diagnose kopieren","›",_host.CopySyncDiagnostics);
        var info=Text($"Aktiv: {_host.SyncOffset(_syncScope):+0;-0;0} ms",11);
        info.SetResourceReference(TextBlock.ForegroundProperty,"GlassAccent");
        info.Margin=new Thickness(8,9,2,4);panel.Children.Add(info);
    }
    private void Rebuild()
    {
        if(_building)return;
        _building=true;
        double previousScroll=MenuScroller.VerticalOffset;
        try{
            Flyout.IsOpen=false;
            MenuItems.Children.Clear();
            Section("▶","Wiedergabe");
            Act("\uE768","Wiedergabe / Pause",
                HotkeyManager.DisplayBinding(_keys.Binding(VisionAction.PlayPause)),
                ()=>_host.ExecuteAction(VisionAction.PlayPause));
            Act("\uE893","Nächster Titel",
                HotkeyManager.DisplayBinding(_keys.Binding(VisionAction.Next)),
                ()=>_host.ExecuteAction(VisionAction.Next));
            Act("\uE892","Vorheriger Titel",
                HotkeyManager.DisplayBinding(_keys.Binding(VisionAction.Previous)),
                ()=>_host.ExecuteAction(VisionAction.Previous));
            Sub("\uE767","Lautstärke","›",p=>{
                FlyoutHeader(p,"SPOTIFY · LAUTSTÄRKE","Nur Spotify, keine globale Windows-Lautstärke");
                foreach(var a in new[]{VisionAction.VolumeUp,VisionAction.VolumeDown,VisionAction.Mute}){
                    var current=a;FlyoutAction(p,HotkeyManager.Label(current),
                        HotkeyManager.DisplayBinding(_keys.Binding(current)),
                        ()=>_host.ExecuteAction(current));
                }
            });
            Section("♪","Lyrics & Sync");
            Toggle("\uE753","Lyrics automatisch suchen",_settings.AutoLyricsSearch,
                ()=>_host.SetLyricsAuto(!_settings.AutoLyricsSearch));
            Sub("\uE774","Automatische Quellenwahl","›",p=>{
                FlyoutHeader(p,"LYRICS · QUELLEN","Automatisch, nur erlaubte Schnittstellen");
                FlyoutInfo(p,"LRCLIB","Word / Line");
                FlyoutInfo(p,"lyrics.ovh","Nur Lesetext");
                if(_host.HasPlainLyrics)
                    FlyoutInfo(p,"Lesetext","Automatische ruhige Leseführung, keine Zeitmarken");
                FlyoutAction(p,"Erneut suchen","↻",()=>{
                    Flyout.IsOpen=false;_host.RescanLyrics();
                });
                FlyoutAction(p,"Lyrics-Stil & manuelle Auswahl","›",()=>{
                    RequestClose();_host.OpenAdvancedSettings("Lyrics");
                });
                FlyoutAction(p,"Synchronisationsdiagnose","Kopieren",()=>{
                    _host.CopySyncDiagnostics();Flyout.IsOpen=false;
                });
                var status=Text(_host.LyricsStatus,10);status.Opacity=.7;
                status.TextWrapping=TextWrapping.Wrap;status.Margin=new Thickness(7,9,4,4);
                p.Children.Add(status);
            });
            Sub("\uE823","Lyrics-Synchronisation","›",SyncFlyout);
            Section("▦","Layout");
            Act("\uE7F3","Position verschieben","›",_host.PrepareMove);
            Sub("\uE78E","Größe","100 %  ›",p=>{
                FlyoutHeader(p,"OVERLAY · GRÖSSE","Größe sofort übernehmen");
                foreach(double factor in new[]{.75,1.0,1.25,1.5}){
                    double selected=factor;
                    FlyoutAction(p,$"{selected*100:0} %",_settings.Scale==factor?"●":"",
                        ()=>{_settings.Scale=selected;ApplyAndSave();});
                }
                FlyoutAction(p,"Weitere Layout-Optionen","›",
                    ()=>{RequestClose();_host.OpenAdvancedSettings("Layout");});
            });
            Sub("\uE8FD","Animationsprofil",_settings.RenderProfile+"  ›",p=>{
                FlyoutHeader(p,"ANIMATION","Leistung und Bewegung");
                foreach(var name in new[]{"Ultra Light","Balanced","Smooth",
                    "High Refresh","Cinematic"}){
                    var chosen=name;
                    FlyoutAction(p,name,_settings.RenderProfile==chosen?"●":"",
                        ()=>{_settings.RenderProfile=chosen;ApplyAndSave();});
                }
            });
            Section("◈","Qualität");
            Sub("\uE753","Vinyl-Qualität",_settings.VinylQuality+"  ›",p=>{
                FlyoutHeader(p,"VINYL-MATERIAL","Rillenqualität und Reflexion");
                foreach(var name in new[]{"UltraLight","Balanced","Cinematic"}){
                    var chosen=name;
                    FlyoutAction(p,name,_settings.VinylQuality==chosen?"●":"",
                        ()=>{_settings.VinylQuality=chosen;ApplyAndSave();});
                }
            });
            Toggle("\uE722","Albumcover dreht mit",_settings.CoverRotates,
                ()=>_settings.CoverRotates=!_settings.CoverRotates);
            Sub("\uE9D9","Visualizer-Empfindlichkeit",
                $"{_settings.AudioSensitivity*100:0} %  ›",p=>{
                FlyoutHeader(p,"FREQUENZRING","Reale Audiodaten; begrenzte Verstärkung");
                foreach(double factor in new[]{.5,.75,1d,1.5,2d}){
                    double selected=factor;
                    FlyoutAction(p,$"{selected*100:0} %",_settings.AudioSensitivity==selected?"●":"",
                        ()=>{_settings.AudioSensitivity=selected;ApplyAndSave();});
                }
                FlyoutAction(p,"Weitere Visualizer-Optionen","›",
                    ()=>{RequestClose();_host.OpenAdvancedSettings("Design");});
            });
            Section("⌨","Tastenkombinationen");
            Act("\uE765","Tastenkombinationen anpassen","›",
                ()=>_host.OpenAdvancedSettings("Keys"));
            Section("⚙","App");
            Toggle("\uE770","Mit Windows starten",_settings.AutoStart,
                ()=>_host.SetAutoStart(!_settings.AutoStart));
            Toggle("\uE7F4","Klickdurchlässig",_settings.ClickThrough,
                ()=>_host.SetClickThrough(!_settings.ClickThrough));
            Act("\uE8F2","Overlay ausblenden","",
                _host.HideOverlayFromSettings);
            Act("\uE7E8","Charli Vision beenden","",_host.ExitFromSettings);
        }finally{
            _building=false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(()=>MenuScroller.ScrollToVerticalOffset(previousScroll)));
        }
    }
    private void ApplyAndSave()
    {
        _host.ApplyControlSettings();
        SettingsStore.Save(_settings);
    }
    internal void SavePreview(string path)
    {
        // Opt-in QA only. Render this WPF window, never other application windows.
        if(!IsLoaded)throw new InvalidOperationException("Glass menu is not loaded");
        UpdateLayout();
        var dpi=VisualTreeHelper.GetDpi(this);
        int width=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX);
        int height=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        if(width<250||height<300||width>1600||height>2400)
            throw new InvalidOperationException("Invalid preview dimensions");
        var image=new RenderTargetBitmap(width,height,
            96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
        image.Render(this);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream=System.IO.File.Create(path);
        encoder.Save(stream);
    }
    internal void CaptureSyncPreview(string path)
    {
        var row=MenuItems.Children.OfType<Button>().FirstOrDefault(b=>
            b.Content is Grid grid &&
            grid.Children.OfType<TextBlock>().Any(t=>
                t.Text=="Lyrics-Synchronisation"));
        if(row is null)throw new InvalidOperationException("Sync row not found");
        row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent,row));
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>{
            try{
                FlyoutShell.Measure(new System.Windows.Size(300,500));
                if(FlyoutShell.ActualWidth<200)
                    FlyoutShell.Arrange(new Rect(0,0,270,
                        Math.Max(280,FlyoutShell.DesiredSize.Height)));
                FlyoutShell.UpdateLayout();
                int width=(int)Math.Ceiling(Math.Max(250,FlyoutShell.RenderSize.Width));
                int height=(int)Math.Ceiling(Math.Max(280,FlyoutShell.RenderSize.Height));
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(FlyoutShell);
                var png=new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=System.IO.File.Create(path);
                png.Save(stream);
            }catch(Exception ex){SettingsStore.Log(ex);}
        }));
    }
    private void RequestClose()
    {
        if(_closing)return;
        _closing=true;
        Flyout.IsOpen=false;
        Close();
    }
    private void CloseClicked(object sender,RoutedEventArgs e)=>RequestClose();
}
