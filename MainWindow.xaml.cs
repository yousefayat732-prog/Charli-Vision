using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace CharliVision;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly MediaController _media = new();
    private readonly AudioEngine _audio = new();
    private readonly MusicTempoEstimator _tempo=new();
    private readonly DesktopLayer _desktop;
    private readonly LyricsService _lyricsService = new();
    private readonly KaraokeWords _karaoke;
    private readonly LyricsSyncSettings _syncSettings=new();
    private string _songKey="";
    private string? _deviceKey;
    private long _trackDetectedStamp;
    private double _lastLyricsReadyMs;
    private int _devicePollCounter;
    private CancellationTokenSource? _lyricsCancellation;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private TrackState? _lastTrack;
    private LyricResult _lyricResult=LyricResult.Unavailable("Noch keine Suche");
    private string _plainLyrics="";
    private double _plainReaderElapsed,_plainScrollPrevious;
    private int _activeLyricIndex = -100;
    private AlbumPalette _appliedPalette = AlbumPalette.Default;
    private readonly ChromaticDesignEngine _chromatic=new();
    private bool _chromaticSubscribed;
    private double _chromaticLastTime;
    internal ChromaticTokens DesignTokens=>_chromatic.Tokens;
    internal event Action<ChromaticTokens>? ChromaticPaletteChanged;
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    // WPF compositor cadence replaces independent 16ms DispatcherTimer.
    // Only frame updates are throttled; MediaController polling remains separate.
    private readonly FramePacingStats _framePacing=new();
    private readonly FramePacingScheduler _frameScheduler=new();
    private readonly float[] _audioFrameLevels=new float[92];
    private bool _renderSubscribed;
    private double _targetFrameMs=16.67;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly WinForms.NotifyIcon _tray = new();
    private string _displayKey = "";
    private bool _polling, _moveMode, _playing, _closing;
    private double _lastTime, _lastDiagnostic, _lastAudioRetry, _lastOcclusionProbe;
    private bool _fullyOccluded;
    private readonly bool _diagnostics = Environment.GetEnvironmentVariable("CHARLI_VISION_DIAGNOSTICS") == "1";
    private SettingsWindow? _settingsWindow;
    private GlassMenuWindow? _glassMenu;
    private HotkeyManager? _hotkeys;
    private AlbumPalette _lastAlbumPalette=AlbumPalette.Default;
    private bool _colorModeWasEnabled=true;
    private double _lastRenderedLyricsScale=1;
    private bool _lastRenderedReducedMotion;
    private bool _lastRenderedWordAnimation=true;
    private bool _lastHighContrast;
    private bool _forceThemeRefresh=true;

    public MainWindow()
    {
        InitializeComponent();
        if(Enum.TryParse<VinylQuality>(_settings.VinylQuality,true,out var quality))
            Vinyl.SetQuality(quality);
        Vinyl.CoverRotates=_settings.CoverRotates;
        _deviceKey=LyricsSyncSettings.DeviceKey();
        _karaoke=new KaraokeWords(WordLyricPanel);
        _chromatic.Changed+=OnChromaticChanged;
        _chromatic.SetTarget(AlbumPalette.Neutral,immediate:true);
        _karaoke.ReducedMotion=_settings.ReducedMotion;
        _karaoke.HighContrast=_settings.HighContrastLyrics;
        _lastHighContrast=_settings.HighContrastLyrics;
        _colorModeWasEnabled=_settings.AlbumColorsEnabled;
        _lastRenderedLyricsScale=_settings.LyricsScale;
        _lastRenderedReducedMotion=_settings.ReducedMotion;
        _lastRenderedWordAnimation=_settings.WordAnimationEnabled;
        _desktop=new DesktopLayer(this);
        _settings.AlwaysOnTop=false;
        _settings.Scale = Math.Clamp(_settings.Scale,.65,1.65);
        Width=980*_settings.Scale; Height=380*_settings.Scale;
        Topmost=false;
        if (_settings.Left is double x && _settings.Top is double y && PositionOnScreen(x,y)) {
            Left=x; Top=y;
        } else {
            Left=Math.Max(0,SystemParameters.WorkArea.Left+(SystemParameters.WorkArea.Width-Width)/2);
            Top=Math.Max(0,SystemParameters.WorkArea.Top+(SystemParameters.WorkArea.Height-Height)/2);
        }
        SourceInitialized += (_,_) => {
            UpdateWindowStyles();_desktop.Attach();
            _hotkeys=new HotkeyManager(this,_settings,ExecuteAction);
        };
        _media.Changed += OnTrackChanged;
        _media.RefreshRequested += () => {
            if(!_closing) Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(async()=>await PollNow()));
        };
        _mediaTimer.Tick += PollTimerTick;
        // Render callback is attached only while overlay is visible.
        Vinyl.MouseLeftButtonDown += Vinyl_MouseLeftButtonDown;
        Loaded += (_,_) => {
            ApplyControlSettings();
            SetupTray();
            SavePlacement();
            _desktop.QueuePlacement();
            StartFrameUpdates(); _mediaTimer.Start();
            _ = PollNow();
            if(_settings.AutomaticUpdateChecks)
                _=VisionUpdateService.Shared.CheckAsync();
        };
        StateChanged += (_,_) => { if (WindowState==WindowState.Minimized) HideOverlay(); };
        LocationChanged += (_,_) => { if (!_closing && IsLoaded) SavePlacement(); };
        Closing += OnClosing;
    }
    private static bool PositionOnScreen(double x,double y) =>
        WinForms.Screen.AllScreens.Any(screen =>
            x >= screen.Bounds.Left-100 && x < screen.Bounds.Right &&
            y >= screen.Bounds.Top-100 && y < screen.Bounds.Bottom);

    private void SetupTray()
    {
        try {
            var filename = Environment.ProcessPath;
            _tray.Icon = filename != null ? System.Drawing.Icon.ExtractAssociatedIcon(filename) :
                System.Drawing.SystemIcons.Application;
        } catch { _tray.Icon=System.Drawing.SystemIcons.Application; }
        _tray.Text="Charli Vision – Spotify Visualizer";
        var menu = new WinForms.ContextMenuStrip();
        void Item(string label,Action action) {
            menu.Items.Add(label,null,(_,_) => Dispatcher.BeginInvoke(action));
        }
        Item("Glasmenü öffnen",OpenSettings);
        Item("Erweiterte Einstellungen",()=>OpenAdvancedSettings());
        Item("Charli Vision anzeigen", ShowOverlay);
        Item("Overlay ausblenden", HideOverlay);
        Item("Klickdurchlässigkeit umschalten",ToggleClickThrough);
        Item("Mit Windows starten umschalten", ToggleAutoStart);
        Item("Lyrics erneut suchen",()=>_ = ManualSearchAsync());
        Item("LRC-Datei importieren",ImportOwnLrc);
        Item("Automatische Lyrics-Suche umschalten",ToggleLyricsAuto);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        Item("Charli Vision beenden",Close);
        _tray.ContextMenuStrip=menu;
        _tray.DoubleClick += (_,_) => Dispatcher.BeginInvoke(ShowOverlay);
        _tray.Visible=true;
    }
    private async void PollTimerTick(object? sender,EventArgs e) => await PollNow();
    private async Task PollNow()
    {
        if (_polling || _closing) return;
        _polling=true;
        try {
            await _media.PollAsync();
            if(++_devicePollCounter>=35){
                _devicePollCounter=0;
                var current=LyricsSyncSettings.DeviceKey();
                if(_deviceKey!=current){_deviceKey=current;_activeLyricIndex=-100;}
            }
        }
        catch(Exception ex) {SettingsStore.Log(ex);}
        finally { _polling=false; }
    }
    private void OnTrackChanged(TrackState state)
    {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.BeginInvoke(() => OnTrackChanged(state));
            return;
        }
        string identity=state.Title+"\n"+state.Artist+"\n"+state.Album;
        bool changed=_displayKey!=identity;
        _displayKey=identity;
        if(changed){
            _trackDetectedStamp=Stopwatch.GetTimestamp();
            _tempo.Reset();Vinyl.TargetRotationRpm=_settings.VinylBaseRpm;
            _songKey=LyricsSyncSettings.SongKey(state.Title,state.Artist,state.Album,state.Duration);
        }
        _playing=state.Playing;
        _lastTrack=state;
        if(_playing && IsVisible) {
            if(!_audio.Running || !_audio.Available){_audio.Stop();_audio.Start();}
        } else _audio.Stop();
        Vinyl.SetTrack(state.Playing,state.Cover,changed);
        if(!changed)return;

        // Cancel stale requests immediately on song change. Never display old lyrics.
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsCancellation=new CancellationTokenSource();
        _lines=Array.Empty<LyricLine>();
        _lyricResult=LyricResult.Unavailable(_settings.AutoLyricsSearch?
            "Automatische Suche":"Automatische Suche deaktiviert");
        _plainLyrics="";_plainReaderElapsed=0;
        _activeLyricIndex=-100;
        bool validSong=!state.Title.Equals("CHARLI VISION",StringComparison.OrdinalIgnoreCase);
        ClearLyrics();
        var token=_lyricsCancellation.Token;
        if(_settings.AutoLyricsSearch || _lyricsService.Library.HasImport(_songKey))
            _=LoadLyricsAsync(state,identity,token);
        _=AnalyzeColorsAsync(state.Cover,identity,token);
    }
    private async Task AnalyzeColorsAsync(System.Windows.Media.Imaging.BitmapSource? art,
        string identity,CancellationToken token)
    {
        try {
            // Only frozen cover data cross thread boundaries.
            AlbumPalette palette=art is null?AlbumPalette.Neutral:
                await Task.Run(()=>AlbumPalette.Analyze(art),token);
            if(token.IsCancellationRequested || _closing || _displayKey!=identity)return;
            _lastAlbumPalette=palette;
            SetChromaticTarget(_settings.AlbumColorsEnabled?palette:AlbumPalette.Neutral);
        } catch(OperationCanceledException) {}
          catch(Exception ex) {SettingsStore.Log(ex);}
    }
    private async Task LoadLyricsAsync(TrackState state,string identity,CancellationToken token,
        bool force=false)
    {
        if(state.Title.Equals("CHARLI VISION",StringComparison.OrdinalIgnoreCase))return;
        try {
            var result=await Task.Run(()=>_lyricsService.GetAsync(
                state.Title,state.Artist,state.Album,state.Duration,token,force),token);
            if(token.IsCancellationRequested||_closing||_displayKey!=identity)return;
            _lyricResult=result;
            _plainLyrics=result.PlainText??"";
            _plainReaderElapsed=0;
            _lines=result.Lines;
            _lastLyricsReadyMs=(Stopwatch.GetTimestamp()-_trackDetectedStamp)*1000d/Stopwatch.Frequency;
            _activeLyricIndex=-100;
            if(!result.Synchronized){
                ClearLyrics();
                if(result.Quality=="Plain" && _settings.PlainLyricsReader)
                    ShowPlainLyrics();
            }else {
                UpdateLyrics(); // First usable line must appear without waiting for word upgrades.
                if(!result.Lines.Any(line=>line.Words is {Count:>0})){
                    var enhanced=await Task.Run(()=>_lyricsService.TryUpgradeWordsAsync(
                        state.Title,state.Artist,state.Album,state.Duration,token),token);
                    if(enhanced!=null && !token.IsCancellationRequested && !_closing &&
                        _displayKey==identity){
                        _lyricResult=enhanced;
                        _lines=enhanced.Lines;
                        _activeLyricIndex=-100;
                        UpdateLyrics();
                    }
                }
            }
        } catch(OperationCanceledException){}
          catch(Exception ex) {
              SettingsStore.Log(ex);
              if(!token.IsCancellationRequested&&_displayKey==identity)ClearLyrics();
          }
    }
    internal string CurrentTrackForAlignment=>
        _lastTrack is null ||
        _lastTrack.Title.Equals("CHARLI VISION",StringComparison.OrdinalIgnoreCase)?
            "":_lastTrack.Artist+" — "+_lastTrack.Title;
    internal bool ImportAlignedLyrics(string lyricsfile)
    {
        if(_closing||string.IsNullOrWhiteSpace(_songKey) ||
            string.IsNullOrWhiteSpace(CurrentTrackForAlignment))return false;
        try {
            _lyricsService.Library.ImportWords(_songKey,lyricsfile);
            _=ManualSearchAsync(); // existing lyrics renderer, no separate timer
            return true;
        }catch(Exception ex){
            SettingsStore.Log(ex);
            return false;
        }
    }
    private LyricsEditorWindow? _lyricsEditor;
    internal void OpenLyricsEditor(Window? owner=null)
    {
        if(_closing || _lastTrack is null ||
            string.IsNullOrWhiteSpace(_songKey))return;
        if(_lyricsEditor is {IsLoaded:true}){
            _lyricsEditor.Activate();return;
        }
        _lyricsEditor=new LyricsEditorWindow(this,_songKey,_lyricResult);
        if(owner is {IsVisible:true})_lyricsEditor.Owner=owner;
        _lyricsEditor.Closed+=(_,_)=>_lyricsEditor=null;
        _lyricsEditor.Show();
    }
    private void BackupCurrentUserLyrics(string key)
    {
        var library=_lyricsService.Library;
        var own=library.GetImport(key);
        var word=library.GetImportWords(key);
        if(string.IsNullOrWhiteSpace(own)&&string.IsNullOrWhiteSpace(word))return;
        var directory=Path.Combine(SettingsStore.DirectoryPath,"LyricsEditBackups");
        Directory.CreateDirectory(directory);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        var stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture);
        if(!string.IsNullOrWhiteSpace(own))
            File.WriteAllText(Path.Combine(directory,hash+"-"+stamp+".lrc"),own);
        if(!string.IsNullOrWhiteSpace(word))
            File.WriteAllText(Path.Combine(directory,hash+"-"+stamp+".yaml"),word);
        // Bound local revision history to the latest 16 files per song.
        var old=Directory.GetFiles(directory,hash+"-*")
            .Select(x=>new FileInfo(x))
            .OrderByDescending(x=>x.CreationTimeUtc).Skip(16);
        foreach(var item in old){try{item.Delete();}catch{}}
        foreach(var item in Directory.GetFiles(directory)
            .Select(x=>new FileInfo(x))
            .OrderByDescending(x=>x.CreationTimeUtc).Skip(250)){
            try{item.Delete();}catch{}
        }
    }
    internal bool SaveEditedLyrics(string key,string raw,string mode)
    {
        if(_closing || _songKey!=key || _lastTrack is null)return false;
        try{
            LyricsEditorDocument.Validate(raw,mode);
            BackupCurrentUserLyrics(key);
            var library=_lyricsService.Library;
            string? originalLrc=library.GetImport(key);
            string? originalWords=library.GetImportWords(key);
            try{
                library.DeleteImport(key);
                if(mode==LyricsEditorDocument.Word)
                    library.ImportWords(key,raw);
                else library.Import(key,raw);
            }catch{
                // A filesystem failure must not discard the previously
                // customized lyrics. Restore both stored formats.
                library.DeleteImport(key);
                if(!string.IsNullOrWhiteSpace(originalLrc))
                    library.Import(key,originalLrc);
                if(!string.IsNullOrWhiteSpace(originalWords))
                    library.ImportWords(key,originalWords);
                throw;
            }
            _=ManualSearchAsync();
            return true;
        }catch(Exception ex){SettingsStore.Log(ex);return false;}
    }
    internal bool RestoreOnlineLyrics(string key)
    {
        if(_closing||_songKey!=key)return false;
        try{
            BackupCurrentUserLyrics(key);
            _lyricsService.Library.DeleteImport(key);
            _=ManualSearchAsync();
            return true;
        }catch(Exception ex){SettingsStore.Log(ex);return false;}
    }
    private void ToggleLyricsAuto()
    {
        _settings.AutoLyricsSearch=!_settings.AutoLyricsSearch;
        SavePlacement();
        if(_settings.AutoLyricsSearch && _lines.Count==0)
            _=ManualSearchAsync();
    }
    private async Task ManualSearchAsync()
    {
        var state=_lastTrack;
        if(state is null || state.Title.Equals("CHARLI VISION",
            StringComparison.OrdinalIgnoreCase))return;
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsCancellation=new CancellationTokenSource();
        _lyricsService.Invalidate(_songKey);
        _lines=Array.Empty<LyricLine>();
        _plainLyrics="";
        _lyricResult=LyricResult.Unavailable("Erneute Suche");
        _activeLyricIndex=-100;
        ClearLyrics();
        await LoadLyricsAsync(state,_displayKey,_lyricsCancellation.Token,true);
    }
    private void ImportOwnLrc()
    {
        if(_lastTrack is null || string.IsNullOrEmpty(_songKey))return;
        try {
            var dialog=new Microsoft.Win32.OpenFileDialog {
                Title="Eigene synchronisierte LRC-Datei importieren",
                Filter="LRC oder TXT (*.lrc;*.txt)|*.lrc;*.txt|LRC (*.lrc)|*.lrc",
                CheckFileExists=true,Multiselect=false
            };
            if(dialog.ShowDialog()!=true)return;
            var file=new FileInfo(dialog.FileName);
            if(file.Length<=0||file.Length>200_000)
                throw new InvalidDataException("Datei ist leer oder größer als 200 KB.");
            string raw=File.ReadAllText(dialog.FileName);
            if(Path.GetExtension(dialog.FileName).Equals(".lrc",
                StringComparison.OrdinalIgnoreCase)&&LyricsService.ParseLrc(raw).Count==0)
                throw new InvalidDataException("LRC-Datei enthält keine gültigen Zeitmarken.");
            _lyricsService.Library.Import(_songKey,raw);
            _=ManualSearchAsync();
        }catch(Exception ex){
            SettingsStore.Log(ex);
            _tray.ShowBalloonTip(2300,"Charli Vision","LRC-Import fehlgeschlagen.",
                WinForms.ToolTipIcon.Warning);
        }
    }
    private void RemoveOwnLrc()
    {
        _lyricsService.Library.DeleteImport(_songKey);
        _=ManualSearchAsync();
    }
    private async Task SearchAlternativesAsync()
    {
        var song=_lastTrack;
        var token=_lyricsCancellation?.Token??CancellationToken.None;
        if(song is null || song.Title.Equals("CHARLI VISION",
            StringComparison.OrdinalIgnoreCase))return;
        string identity=_displayKey;
        try {
            var results=await Task.Run(()=>_lyricsService.FindAlternativesAsync(
                song.Title,song.Artist,song.Album,song.Duration,token),token);
            if(token.IsCancellationRequested||_closing||_displayKey!=identity)return;
            _tray.ShowBalloonTip(2300,"Charli Vision",
                results.Count>0?
                    $"{results.Count} passende Versionen gefunden. Rechtsklick für Auswahl.":
                    "Keine weiteren sicher passenden Versionen gefunden.",
                WinForms.ToolTipIcon.Info);
        }catch(OperationCanceledException){}
        catch(Exception ex){SettingsStore.Log(ex);}
    }
    private async Task ChooseLyricsAsync(int id)
    {
        var track=_lastTrack;
        if(track is null || _lyricsCancellation is null)return;
        string identity=_displayKey;
        var token=_lyricsCancellation.Token;
        try {
            var chosen=await Task.Run(()=>_lyricsService.ChooseAlternativeAsync(
                id,track.Title,track.Artist,track.Album,track.Duration,token),token);
            if(chosen is null||_closing||token.IsCancellationRequested||
                identity!=_displayKey)return;
            _lyricResult=chosen;_plainLyrics=chosen.PlainText??"";_plainReaderElapsed=0;
            _lines=chosen.Lines;_activeLyricIndex=-100;
            ClearLyrics();
            if(chosen.Synchronized)UpdateLyrics();
            else if(chosen.Quality=="Plain"&&_settings.PlainLyricsReader)
                ShowPlainLyrics();
        }catch(OperationCanceledException){}
        catch(Exception ex){SettingsStore.Log(ex);}
    }
    private void ShowPlainLyrics()
    {
        if(!_settings.PlainLyricsReader||string.IsNullOrWhiteSpace(_plainLyrics)){
            ClearLyrics();return;
        }
        // A reading-only, automatically scrolling text view, NOT a fabricated
        // time-alignment algorithm. Playback position never highlights words.
        var plain=_plainLyrics.Replace("\r","").Trim();
        if(plain.Length==0){ClearLyrics();return;}
        _karaoke.Clear();
        _plainReaderElapsed=0;
        _plainScrollPrevious=0;
        PlainLyricsText.Text=plain.Length>16000?plain[..16000]:plain;
        PlainLyricsReader.ScrollToTop();
        PlainLyricsReader.Visibility=Visibility.Visible;
        ActiveLyric.Visibility=Visibility.Collapsed;
        WordLyricPanel.Visibility=Visibility.Collapsed;
        PreviousLyric.Text="";
        NextLyric.Text="";
        PreviousLyric.Visibility=Visibility.Collapsed;
        NextLyric.Visibility=Visibility.Collapsed;
        TitleRegion.Visibility=Visibility.Visible;
    }
    private void UpdatePlainReader(double dt)
    {
        if(PlainLyricsReader.Visibility!=Visibility.Visible||!_playing)return;
        _plainReaderElapsed+=dt;
        if(_plainReaderElapsed-_plainScrollPrevious<.06)return;
        _plainScrollPrevious=_plainReaderElapsed;
        // Slow constant reading pace; never tied to guessed lyric timestamps.
        double offset=Math.Min(PlainLyricsReader.ScrollableHeight,
            _plainReaderElapsed*12);
        if(Math.Abs(offset-PlainLyricsReader.VerticalOffset)>.3)
            PlainLyricsReader.ScrollToVerticalOffset(offset);
    }
    internal bool HasPlainLyrics=>_lyricResult.Quality=="Plain" &&
        _settings.PlainLyricsReader && !string.IsNullOrWhiteSpace(_plainLyrics);
    private void ClearLyrics(bool resetIndex=true)
    {
        if(resetIndex)_activeLyricIndex=-100;
        TitleRegion.Visibility=Visibility.Collapsed;
        PlainLyricsReader.Visibility=Visibility.Collapsed;
        PlainLyricsText.Text="";
        _plainReaderElapsed=0;_plainScrollPrevious=0;
        PreviousLyric.Visibility=Visibility.Visible;
        NextLyric.Visibility=Visibility.Visible;
        PreviousLyric.Text="";
        ActiveLyric.BeginAnimation(OpacityProperty,null);
        ActiveLyric.Text="";
        ActiveLyric.Visibility=Visibility.Visible;
        ActiveLyric.Opacity=1;
        NextLyric.Text="";
        _karaoke.Clear();
    }
    private static void AnimateLyricEntrance(UIElement element,
        double fromY,double opacity,double durationMs)
    {
        var duration=TimeSpan.FromMilliseconds(Math.Clamp(durationMs,45,210));
        var ease=new QuadraticEase{EasingMode=EasingMode.EaseOut};
        var slide=new TranslateTransform(0,fromY);
        element.RenderTransform=slide;
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(fromY,0,duration){EasingFunction=ease});
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(Math.Max(.40,opacity-.23),opacity,duration){
                EasingFunction=ease
            });
    }
    // All lyric positions remain anchored to their actual source timestamps.
    // Tempo only affects visual transition duration, never word/line selection.
    internal static double LineTransitionMs(double forwardGap,double backwardGap,double? bpm)
    {
        double milliseconds=Math.Min(forwardGap,backwardGap)*.16;
        if(bpm is double reliable && double.IsFinite(reliable)){
            if(reliable>=140)milliseconds*=.84;
            else if(reliable<=80)milliseconds*=1.12;
        }
        return Math.Clamp(milliseconds,55,190);
    }
    private void UpdateLyrics()
    {
        if(_lines.Count==0)return;
        var position=_syncSettings.Apply(_media.Clock.Estimated(),_songKey,_deviceKey);
        if(position is null){
            if(TitleRegion.Visibility!=Visibility.Collapsed)ClearLyrics();
            return;
        }
        int index=LyricsService.ActiveAt(_lines,position.Value);
        if(index==_activeLyricIndex){
            if(index>=0 && _lines[index].Words is {Count:>0})
                _karaoke.Update(position.Value);
            return;
        }
        _activeLyricIndex=index;
        string current=index<0?"":_lines[index].Text;
        if(string.IsNullOrWhiteSpace(current)){
            ClearLyrics(false);return;
        }
        string previous=index>0?_lines[index-1].Text:"";
        string next=index+1<_lines.Count?_lines[index+1].Text:"";
        PreviousLyric.Text=previous;
        NextLyric.Text=next;
        PreviousLyric.FontSize=(previous.Length>75?17:22)*_settings.LyricsScale;
        NextLyric.FontSize=(next.Length>75?17:22)*_settings.LyricsScale;
        TitleRegion.Visibility=Visibility.Visible;
        var currentLine=_lines[index];
        bool wordTiming=_karaoke.Setup(currentLine);
        if(wordTiming){
            ActiveLyric.Visibility=Visibility.Collapsed;
            _karaoke.ApplyTheme(_appliedPalette);
            _karaoke.Update(position.Value);
        } else {
            ActiveLyric.Visibility=Visibility.Visible;
            ActiveLyric.Text=current;
            ActiveLyric.FontWeight=FontWeights.ExtraBold;
            ActiveLyric.FontSize=(current.Length switch {
                <23=>50,<39=>43,<59=>36,<100=>29,_=>23
            })*_settings.LyricsScale;
        }
        double forwardGap=index+1<_lines.Count ?
            Math.Max(60,(_lines[index+1].Time-currentLine.Time).TotalMilliseconds):700;
        double backwardGap=index>0?
            Math.Max(60,(currentLine.Time-_lines[index-1].Time).TotalMilliseconds):
            forwardGap;
        // Rapid lyrics get a short transition. The correct word is already
        // selected before animation begins, avoiding delayed highlighting.
        double timeMs=LineTransitionMs(forwardGap,backwardGap,_tempo.EstimatedBpm);
        // On fast tracks transitions shorten, never delaying word timestamps.
        if(_settings.ReducedMotion){
            ActiveLyric.BeginAnimation(OpacityProperty,null);
            ActiveLyric.Opacity=1;
            ActiveLyric.RenderTransform=Transform.Identity;
            PreviousLyric.BeginAnimation(OpacityProperty,null);
            PreviousLyric.Opacity=_settings.HighContrastLyrics?.88:.62;
            NextLyric.BeginAnimation(OpacityProperty,null);
            NextLyric.Opacity=_settings.HighContrastLyrics?.88:.64;
            WordLyricPanel.BeginAnimation(OpacityProperty,null);
            WordLyricPanel.Opacity=1;
            WordLyricPanel.RenderTransform=Transform.Identity;
        }else {
            if(!wordTiming){
                ActiveLyric.BeginAnimation(OpacityProperty,new DoubleAnimation(.5,1,
                    TimeSpan.FromMilliseconds(timeMs)));
                var movement=new TranslateTransform(0,7);
                ActiveLyric.RenderTransform=movement;
                movement.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(7,0,
                    TimeSpan.FromMilliseconds(timeMs)) {
                    EasingFunction=new QuadraticEase{EasingMode=EasingMode.EaseOut}
                });
            }
            if(wordTiming)
                AnimateLyricEntrance(WordLyricPanel,6,1,timeMs);
            else{
                WordLyricPanel.BeginAnimation(OpacityProperty,null);
                WordLyricPanel.RenderTransform=Transform.Identity;
            }
            AnimateLyricEntrance(PreviousLyric,-3,
                _settings.HighContrastLyrics?.88:.62,timeMs*.8);
            AnimateLyricEntrance(NextLyric,3,
                _settings.HighContrastLyrics?.88:.64,timeMs*.8);
        }
    }
    private void SetChromaticTarget(AlbumPalette palette)
    {
        _chromatic.SetTarget(palette,_settings.ReducedMotion);
        if(!_chromatic.Transitioning)return;
        _chromaticLastTime=_clock.Elapsed.TotalSeconds;
        if(_chromaticSubscribed)return;
        // One shared, short-lived compositor subscription for album colors.
        // This continues even when the desktop overlay is hidden, as long as
        // one of our WPF windows is visible.
        CompositionTarget.Rendering+=ChromaticRender;
        _chromaticSubscribed=true;
    }
    private void ChromaticRender(object? sender,EventArgs e)
    {
        if(_closing)return;
        double now=_clock.Elapsed.TotalSeconds;
        _chromatic.Tick(now-_chromaticLastTime,_settings.ReducedMotion);
        _chromaticLastTime=now;
        if(!_chromatic.Transitioning){
            CompositionTarget.Rendering-=ChromaticRender;
            _chromaticSubscribed=false;
        }
    }
    private void OnChromaticChanged(ChromaticTokens tokens)
    {
        Vinyl.SetPaletteImmediate(tokens.Palette);
        _forceThemeRefresh=true;
        ApplyTheme();
        ChromaticPaletteChanged?.Invoke(tokens);
    }
    private void ApplyTheme()
    {
        var palette=_chromatic.Tokens.Palette;
        // Avoid constantly allocating brushes for negligible color changes.
        var a=palette.Accent;
        var prior=_appliedPalette.Accent;
        if(!_forceThemeRefresh &&
            Math.Abs(a.R-prior.R)+Math.Abs(a.G-prior.G)+Math.Abs(a.B-prior.B)<5)return;
        _forceThemeRefresh=false;
        _appliedPalette=palette;
        var color=_settings.HighContrastLyrics?Colors.White:
            AlbumPalette.Mix(Colors.White,palette.Highlight,.55);
        var primary=new SolidColorBrush(color);primary.Freeze();
        ActiveLyric.Foreground=primary;
        var quiet=_settings.HighContrastLyrics?
            Color.FromRgb(224,232,241):
            AlbumPalette.Mix(Color.FromRgb(175,182,192),palette.Secondary,.20);
        var muted=new SolidColorBrush(quiet);muted.Freeze();
        PreviousLyric.Foreground=muted;
        NextLyric.Foreground=muted;
        PlainLyricsText.Foreground=primary;
        _karaoke.ApplyTheme(palette);
        _karaoke.GlowStrength=_settings.LyricsGlow;
        if(ActiveLyric.Effect is DropShadowEffect glow){
            glow.Color=palette.Accent;glow.Opacity=_settings.LyricsGlow;
        }
    }
    private void StartFrameUpdates()
    {
        _frameScheduler.Reset();
        _lastTime=_clock.Elapsed.TotalSeconds;
        _framePacing.Reset();
        if(_renderSubscribed)return;
        CompositionTarget.Rendering+=RenderTick;
        _renderSubscribed=true;
    }
    private void StopFrameUpdates()
    {
        if(!_renderSubscribed)return;
        CompositionTarget.Rendering-=RenderTick;
        _renderSubscribed=false;
        _frameScheduler.Reset();
        _framePacing.Reset();
    }
    private void RenderTick(object? sender,EventArgs e)
    {
        if(!IsVisible||_closing)return;
        var now=_clock.Elapsed.TotalSeconds;
        if (_playing && !_audio.Available && now-_lastAudioRetry>5) {
            _lastAudioRetry=now; _audio.Stop(); _audio.Start();
        }
        if(now-_lastOcclusionProbe>.85){
            _lastOcclusionProbe=now;
            _fullyOccluded=_desktop.IsFullyOccluded();
        }
        // Maintain the selected compositor cadence even behind other windows
        // and while Spotify is paused. Occlusion is diagnostic only, never
        // a reason to reduce the vinyl animation target to ~10 FPS.
        // WPF/Windows may still limit physically presented frames.
        double desired=_settings.RenderProfile switch{
            "Ultra Light"=>1000d/30d,
            "High Refresh"=>1000d/180d,
            "Balanced"=>1000d/60d,
            "Smooth"=>1000d/120d,
            "Cinematic"=>1000d/120d,
            _=>1000d/120d
        };
        _targetFrameMs=desired;
        if(!_frameScheduler.ShouldUpdate(now,desired))return;
        _framePacing.AddFrame(Stopwatch.GetTimestamp(),desired);
        double dt=Math.Max(0,now-_lastTime);
        _lastTime=now;
        float[]? levels=null;
        if(_audio.Available&&_playing){
            _audio.SnapshotInto(_audioFrameLevels);
            levels=_audioFrameLevels;
            if(_settings.AdaptiveVinylTempoEnabled)
                _tempo.Observe(now,_audioFrameLevels);
            Vinyl.TargetRotationRpm=ComputeVinylRpm();
        }
        Vinyl.Tick(dt,levels);
        if(_forceThemeRefresh)ApplyTheme();
        UpdatePlainReader(dt);
        UpdateLyrics();
        if (_diagnostics && now-_lastDiagnostic > 2) {
            _lastDiagnostic=now;
            var stats=_framePacing.GetSample();
            try {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.WriteAllText(Path.Combine(SettingsStore.DirectoryPath,"test-status.txt"),
                    $"SpotifyPlaying={_playing}; LoopbackAvailable={_audio.Available}; Peak={levels?.Max():F4}; AudioRms={_audio.LastRms:F5}; AudioGain={_audio.NormalizationGain:F1}; VinylRenderMs={Vinyl.LastRenderMs:F2}; GrooveCount={Vinyl.GrooveCount}; VinylQuality={Vinyl.Quality}; FullyOccluded={_fullyOccluded}; ClockValid={_media.Clock.Valid}; ClockSec={_media.Clock.Estimated()?.TotalSeconds:F2}; DurationSec={_media.Clock.Duration.TotalSeconds:F0}; LyricsCount={_lines.Count}; WordSyncedLines={_lines.Count(x=>x.Words is {Count:>0})}; LastWord={_karaoke.CurrentWordIndex}; ClockSampleAgeMs={_media.Clock.SampleAgeMs:F1}; ClockErrorMs={_media.Clock.LastErrorMs:F1}; PollMs={_media.LastPollMs:F1}; MetadataMs={_media.LastMetadataQueryMs:F1}; RecognizeMs={_media.LastRecognitionDelayMs:F1}; LyricsReadyMs={_lastLyricsReadyMs:F1}; LookupMs={_lyricsService.LastLookupMs:F1}; CacheHit={_lyricsService.LastCacheHit}; LyricsProvider={_lyricResult.Source}; LyricsQuality={_lyricResult.Quality}; ProviderRequests={_lyricsService.LastProviderAttempts}; LyricsOffsetMs={_syncSettings.EffectiveMs(_songKey,_deviceKey)}; TextVisible={TitleRegion.Visibility==Visibility.Visible}; Palette={Vinyl.CurrentPalette.Accent}; FPSIntervalMs={_targetFrameMs:F2}; FrameMeanMs={stats.MeanMs:F2}; FrameP95Ms={stats.P95Ms:F2}; FrameP99Ms={stats.P99Ms:F2}; FrameMaxMs={stats.MaxMs:F2}; FrameSampleCount={_framePacing.Count}; EstimatedFrameMisses={_framePacing.EstimatedMisses}");
            } catch { }
        }
        // Render cadence is selected before Tick; no timer to reschedule.
    }
    private void Vinyl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.PositionLocked)return;
        if (_moveMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) {
            _moveMode=false;
            Cursor=Cursors.Arrow;
            try { DragMove(); SavePlacement(); } catch(Exception ex) {SettingsStore.Log(ex);}
            e.Handled=true;
        }
    }
    private async void Vinyl_MouseLeftButtonUp(object sender,MouseButtonEventArgs e)
    {
        if (e.Handled || _moveMode || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        try { await _media.Toggle(); } catch(Exception ex) {SettingsStore.Log(ex); }
    }
    private void TitleRegion_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)
    {
        if (_settings.PositionLocked || (!_moveMode &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) return;
        _moveMode=false;
        try { DragMove(); SavePlacement(); } catch(Exception ex) {SettingsStore.Log(ex);}
        e.Handled=true;
    }
    private void Vinyl_MouseWheel(object sender,MouseWheelEventArgs e)
    {
        if (!_media.ChangeSpotifyVolume(e.Delta>0?.05f:-.05f)) NotifyUnavailable();
        e.Handled=true;
    }
    private void NotifyUnavailable()
    {
        _tray.ShowBalloonTip(1500,"Charli Vision",
            "Spotify-Lautstärke nicht verfügbar. Windows-Lautstärke wurde nicht verändert.",
            WinForms.ToolTipIcon.Info);
    }
    private void Vinyl_MouseRightButtonUp(object sender,MouseButtonEventArgs e)
    {
        OpenSettings();
        e.Handled=true;
    }
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu {
            Background=new SolidColorBrush(Color.FromRgb(22,19,21)),
            Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Vinyl.CurrentPalette.Accent)
        };
        void Add(string title,Action action,bool? check=null) {
            var item=new MenuItem { Header=title, Foreground=Brushes.White };
            if (check.HasValue) {item.IsCheckable=true;item.IsChecked=check.Value;}
            item.Click+=(_,_) => action();
            menu.Items.Add(item);
        }
        Add("▶  Wiedergabe / Pause",() => _ = SafeMedia(_media.Toggle));
        Add("⏭  Nächster Titel",() => _ = SafeMedia(_media.Next));
        Add("⏮  Vorheriger Titel",() => _ = SafeMedia(_media.Previous));
        menu.Items.Add(new Separator());
        Add("Lautstärke +",() => {if(!_media.ChangeSpotifyVolume(.05f))NotifyUnavailable();});
        Add("Lautstärke −",() => {if(!_media.ChangeSpotifyVolume(-.05f))NotifyUnavailable();});
        Add("Stummschalten",() => {if(!_media.ChangeSpotifyVolume(0,true))NotifyUnavailable();});
        menu.Items.Add(new Separator());
        var lyricsMenu=new MenuItem {
            Header=$"Lyrics & Suche — {_lyricResult.Source} ({_lyricResult.Quality})",
            Foreground=Brushes.White
        };
        menu.Items.Add(lyricsMenu);
        void AddLyricsAction(string title,Action run,bool? selected=null) {
            var item=new MenuItem{Header=title,Foreground=Brushes.White};
            if(selected.HasValue){item.IsCheckable=true;item.IsChecked=selected.Value;}
            item.Click+=(_,_)=>run();
            lyricsMenu.Items.Add(item);
        }
        AddLyricsAction("Automatisch suchen",ToggleLyricsAuto,_settings.AutoLyricsSearch);
        AddLyricsAction("Nur-Lesetext anzeigen (ohne Synchronisierung)",()=>{
            _settings.PlainLyricsReader=!_settings.PlainLyricsReader;
            SavePlacement();
            if(_lyricResult.Quality=="Plain")ShowPlainLyrics();
        },_settings.PlainLyricsReader);
        AddLyricsAction("Erneut im Internet suchen",()=>_ = ManualSearchAsync());
        AddLyricsAction("Alternative LRCLIB-Versionen suchen",()=>_ = SearchAlternativesAsync());
        AddLyricsAction("Eigene LRC/TXT-Datei importieren",ImportOwnLrc);
        if(_lyricsService.Library.HasImport(_songKey))
            AddLyricsAction("Eigenen Import entfernen",RemoveOwnLrc);
        var alternatives=_lyricsService.Alternatives(_songKey);
        if(alternatives.Count>0){
            var options=new MenuItem {
                Header=$"Andere passende LRCLIB-Versionen ({alternatives.Count})",
                Foreground=Brushes.White
            };
            lyricsMenu.Items.Add(options);
            foreach(var alternative in alternatives.Take(12)){
                int id=alternative.Id;
                string label=$"{alternative.Quality} · {alternative.Title} · "+
                    $"{alternative.Album} · {alternative.Duration:F0}s";
                if(label.Length>95)label=label[..95]+"…";
                var item=new MenuItem{Header=label,Foreground=Brushes.White};
                item.Click+=(_,_)=>_ = ChooseLyricsAsync(id);
                options.Items.Add(item);
            }
            if(_lyricsService.Library.SelectedId(_songKey)>0)
                AddLyricsAction("Manuelle Auswahl zurücksetzen",()=>{
                    _lyricsService.Library.RemoveSelection(_songKey);
                    _=ManualSearchAsync();
                });
        }
        lyricsMenu.Items.Add(new Separator());
        lyricsMenu.Items.Add(new MenuItem{
            Header=$"Status: {_lyricResult.Status}",
            IsEnabled=false
        });
        menu.Items.Add(new Separator());
        var offsetMenu=new MenuItem {
            Header=$"Lyrics-Synchronisation (gesamt { _syncSettings.EffectiveMs(_songKey,_deviceKey):+0;-0;0} ms)",
            Foreground=Brushes.White
        };
        menu.Items.Add(offsetMenu);
        void OffsetItem(ItemCollection entries,string label,Action action) {
            var control=new MenuItem {Header=label,Foreground=Brushes.White};
            control.Click+=(_,_) => {
                action();
                _activeLyricIndex=-100;
                UpdateLyrics();
            };
            entries.Add(control);
        }
        var info=new MenuItem {Header="+ früher / − später",IsEnabled=false};
        offsetMenu.Items.Add(info);
        var songOffsets=new MenuItem {
            Header=$"Dieser Song ({_syncSettings.SongMs(_songKey):+0;-0;0} ms)",
            Foreground=Brushes.White
        };
        offsetMenu.Items.Add(songOffsets);
        foreach(int ms in new[]{50,100,500}){
            int delta=ms;
            OffsetItem(songOffsets.Items,$"+{delta} ms früher",()=>_syncSettings.AdjustSong(_songKey,delta));
            OffsetItem(songOffsets.Items,$"−{delta} ms später",()=>_syncSettings.AdjustSong(_songKey,-delta));
        }
        OffsetItem(songOffsets.Items,"Song-Offset zurücksetzen",()=>_syncSettings.ResetSong(_songKey));

        var globalOffsets=new MenuItem {
            Header=$"Global ({_syncSettings.GlobalMs:+0;-0;0} ms)",
            Foreground=Brushes.White
        };
        offsetMenu.Items.Add(globalOffsets);
        foreach(int ms in new[]{50,100,500}){
            int delta=ms;
            OffsetItem(globalOffsets.Items,$"+{delta} ms früher",()=>_syncSettings.AdjustGlobal(delta));
            OffsetItem(globalOffsets.Items,$"−{delta} ms später",()=>_syncSettings.AdjustGlobal(-delta));
        }
        OffsetItem(globalOffsets.Items,"Global zurücksetzen",_syncSettings.ResetGlobal);
        if(_deviceKey is not null) {
            var deviceOffsets=new MenuItem {
                Header=$"Audiogerät ({_syncSettings.DeviceMs(_deviceKey):+0;-0;0} ms)",
                Foreground=Brushes.White
            };
            offsetMenu.Items.Add(deviceOffsets);
            foreach(int ms in new[]{50,100,500}){
                int delta=ms;
                OffsetItem(deviceOffsets.Items,$"+{delta} ms früher",
                    ()=>_syncSettings.AdjustDevice(_deviceKey,delta));
                OffsetItem(deviceOffsets.Items,$"−{delta} ms später",
                    ()=>_syncSettings.AdjustDevice(_deviceKey,-delta));
            }
            OffsetItem(deviceOffsets.Items,"Gerät zurücksetzen",
                ()=>_syncSettings.ResetDevice(_deviceKey));
        }
        Add("Sync-Diagnose kopieren",()=> {
            try {
                System.Windows.Clipboard.SetText(
                    $"Clock={_media.Clock.Estimated()?.TotalSeconds:F2}s; "+
                    $"TimestampAge={_media.Clock.SampleAgeMs:F1}ms; "+
                    $"ClockCorrection={_media.Clock.LastErrorMs:F1}ms; "+
                    $"LyricsOffset={_syncSettings.EffectiveMs(_songKey,_deviceKey)}ms; "+
                    $"Line={_activeLyricIndex}; Word={_karaoke.CurrentWordIndex}; "+
                    $"SongRecognition={_media.LastRecognitionDelayMs:F1}ms; "+
                    $"LyricsLoad={_lastLyricsReadyMs:F1}ms; "+
                    $"LyricsLookup={_lyricsService.LastLookupMs:F1}ms");
            }catch(Exception ex){SettingsStore.Log(ex);}
        });
        menu.Items.Add(new Separator());
        Add("Position verschieben (nächster Linksklick)",() => { _moveMode=true;Cursor=Cursors.SizeAll; });
        Add("Größe: 75 %",() => SetScale(.75));
        Add("Größe: 100 %",() => SetScale(1));
        Add("Größe: 125 %",() => SetScale(1.25));
        Add("Bewegungen reduzieren",()=> {
            _settings.ReducedMotion=!_settings.ReducedMotion;
            _karaoke.ReducedMotion=_settings.ReducedMotion;
            SavePlacement();
        },_settings.ReducedMotion);
        var profile=new MenuItem { Header="Animationsprofil", Foreground=Brushes.White };
        menu.Items.Add(profile);
        foreach(string name in new[]{"Ultra Light","Balanced","Smooth",
            "High Refresh","Cinematic"}){
            string value=name;
            var item=new MenuItem{Header=value,IsCheckable=true,
                IsChecked=_settings.RenderProfile==value,Foreground=Brushes.White};
            item.Click+=(_,_)=>{_settings.RenderProfile=value;SavePlacement();};
            profile.Items.Add(item);
        }
        var qualityMenu=new MenuItem {Header="Vinyl-Qualität",Foreground=Brushes.White};
        menu.Items.Add(qualityMenu);
        foreach(var option in new[]{
            (VinylQuality.UltraLight,"Ultra Light"),
            (VinylQuality.Balanced,"Balanced"),
            (VinylQuality.Cinematic,"Cinematic")
        }){
            var choice=option;
            var item=new MenuItem {
                Header=choice.Item2,Foreground=Brushes.White,
                IsCheckable=true,IsChecked=Vinyl.Quality==choice.Item1
            };
            item.Click+=(_,_)=>{
                Vinyl.SetQuality(choice.Item1);
                _settings.VinylQuality=choice.Item1.ToString();
                SavePlacement();
            };
            qualityMenu.Items.Add(item);
        }
        Add("Albumcover dreht mit",()=>{
            Vinyl.CoverRotates=!Vinyl.CoverRotates;
            _settings.CoverRotates=Vinyl.CoverRotates;
            Vinyl.InvalidateVisual();
            SavePlacement();
        },_settings.CoverRotates);
        Add("Mit Windows starten",ToggleAutoStart,_settings.AutoStart);
        Add("Klickdurchlässig (Tray zur Steuerung)",ToggleClickThrough,_settings.ClickThrough);
        menu.Items.Add(new Separator());
        Add("Overlay ausblenden",HideOverlay);
        Add("Charli Vision beenden",Close);
        return menu;
    }
    internal AlbumPalette ActivePalette=>_chromatic.Tokens.Palette;
    internal string LyricsStatus=>$"{_lyricResult.Source} · {_lyricResult.Quality} · {_lyricResult.Status}";

    internal void OpenSettings()
    {
        if(_closing||_hotkeys is null)return;
        if(_glassMenu is not null){_glassMenu.ActivateMenu();return;}
        var mouse=WinForms.Cursor.Position;
        var popup=new GlassMenuWindow(this,_settings,_hotkeys,
            new Point(mouse.X,mouse.Y));
        _glassMenu=popup;
        popup.Closed+=(_,_)=>_glassMenu=null;
        popup.Show();popup.Activate();
    }
    internal void OpenAdvancedSettings(string section="Playback")
    {
        if(_closing)return;
        if(_settingsWindow!=null){
            if(_settingsWindow.WindowState==WindowState.Minimized)
                _settingsWindow.WindowState=WindowState.Normal;
            _settingsWindow.Show();_settingsWindow.Activate();
            _settingsWindow.NavigateTo(section);return;
        }
        if(_hotkeys is null)return;
        var panel=new SettingsWindow(this,_settings,_hotkeys);
        _settingsWindow=panel;
        panel.Closed+=(_,_)=>_settingsWindow=null;
        panel.Show();panel.Activate();
        panel.NavigateTo(section);
    }
    internal void PreviewSettingsSection(string section)=>_settingsWindow?.NavigateTo(section);
    internal void CaptureGlassPreview()=>_glassMenu?.SavePreview(
        Path.Combine(SettingsStore.DirectoryPath,"glass-preview.png"));
    internal void CaptureSettingsPreview()=>_settingsWindow?.SavePreview(
        Path.Combine(SettingsStore.DirectoryPath,"chromatic-settings-preview.png"));
    internal void CaptureGlassSyncPreview()=>_glassMenu?.CaptureSyncPreview(
        Path.Combine(SettingsStore.DirectoryPath,"glass-sync-preview.png"));
    internal double ComputeVinylRpm()
    {
        double fixedRpm=Math.Clamp(_settings.VinylBaseRpm,24,52);
        if(!_settings.AdaptiveVinylTempoEnabled || _tempo.EstimatedBpm is null)
            return fixedRpm;
        double delta=MusicTempoEstimator.TargetRpm(_tempo.EstimatedBpm)-33.333;
        return Math.Clamp(fixedRpm+delta,24,52);
    }
    internal void ApplyControlSettings()
    {
        Vinyl.TargetRotationRpm=ComputeVinylRpm();
        Vinyl.CoverRotates=_settings.CoverRotates;
        Vinyl.CenterTransparent=_settings.VinylCenterTransparent;
        Vinyl.RotationEnabled=_settings.VinylRotationEnabled;
        Vinyl.SmoothingFactor=_settings.AudioSmoothing;
        _audio.Sensitivity=_settings.AudioSensitivity;
        _karaoke.ReducedMotion=_settings.ReducedMotion || !_settings.WordAnimationEnabled;
        if(_lastHighContrast!=_settings.HighContrastLyrics){
            _lastHighContrast=_settings.HighContrastLyrics;
            _karaoke.HighContrast=_lastHighContrast;
            _forceThemeRefresh=true;
            _activeLyricIndex=-100;
            if(_lines.Count>0)UpdateLyrics();
        }
        _karaoke.GlowStrength=_settings.LyricsGlow;
        _karaoke.SizeScale=_settings.LyricsScale;
        if(Enum.TryParse<VinylQuality>(_settings.VinylQuality,true,out var quality))
            Vinyl.SetQuality(quality);
        if(Math.Abs(Width-980*_settings.Scale)>.5)
            Width=980*_settings.Scale;
        if(Math.Abs(Height-380*_settings.Scale)>.5)
            Height=380*_settings.Scale;
        double disc=350*_settings.VinylScale;
        Vinyl.Width=disc;Vinyl.Height=disc;
        TitleRegion.Margin=new Thickness(12+_settings.LyricsSpacing,14,32,14);
        if(_colorModeWasEnabled!=_settings.AlbumColorsEnabled){
            _colorModeWasEnabled=_settings.AlbumColorsEnabled;
            SetChromaticTarget(_settings.AlbumColorsEnabled?_lastAlbumPalette:AlbumPalette.Neutral);
        }
        if(ActiveLyric.Effect is DropShadowEffect glow)
            glow.Opacity=_settings.LyricsGlow;
        bool lyricVisualChanged=Math.Abs(_lastRenderedLyricsScale-_settings.LyricsScale)>.001 ||
            _lastRenderedReducedMotion!=_settings.ReducedMotion ||
            _lastRenderedWordAnimation!=_settings.WordAnimationEnabled;
        _lastRenderedLyricsScale=_settings.LyricsScale;
        _lastRenderedReducedMotion=_settings.ReducedMotion;
        _lastRenderedWordAnimation=_settings.WordAnimationEnabled;
        if(lyricVisualChanged){_activeLyricIndex=-100;if(_lines.Count>0)UpdateLyrics();}
    }
    internal void ExecuteAction(VisionAction action)
    {
        if(_closing)return;
        switch(action){
            case VisionAction.PlayPause: _=SafeMedia(_media.Toggle);break;
            case VisionAction.Next: _=SafeMedia(_media.Next);break;
            case VisionAction.Previous: _=SafeMedia(_media.Previous);break;
            case VisionAction.VolumeUp:
                if(!_media.ChangeSpotifyVolume(.05f))NotifyUnavailable();break;
            case VisionAction.VolumeDown:
                if(!_media.ChangeSpotifyVolume(-.05f))NotifyUnavailable();break;
            case VisionAction.Mute:
                if(!_media.ChangeSpotifyVolume(0,true))NotifyUnavailable();break;
            case VisionAction.LyricsEarlier: AdjustLyrics("Song",50);break;
            case VisionAction.LyricsLater: AdjustLyrics("Song",-50);break;
            case VisionAction.ToggleOverlay:
                if(IsVisible)HideOverlay();else ShowOverlay();break;
            case VisionAction.OpenSettings:OpenSettings();break;
            case VisionAction.PositionMode:
                ShowOverlay();PrepareMove();break;
            case VisionAction.CycleProfile:
                string[] profiles={"Balanced","Smooth","High Refresh"};
                int current=Array.IndexOf(profiles,_settings.RenderProfile);
                _settings.RenderProfile=profiles[(current+1)%profiles.Length];
                SavePlacement();break;
        }
    }
    internal string GetLiveDiagnostics()
    {
        var frames=_framePacing.GetSample();
        using var current=Process.GetCurrentProcess();
        string timing=frames.Samples>=20?
            $"WPF-Update-Intervall: Ø {frames.MeanMs:F1} ms | "+
            $"P95 {frames.P95Ms:F1} ms | P99 {frames.P99Ms:F1} ms "+
            $"({frames.Samples} Samples, geschätzte Budgetausfälle {frames.EstimatedMisses})":
            "WPF-Framezeiten: nicht messbar – Overlay verdeckt, pausiert oder zu wenige Samples";
        return $"Spotify: {(_lastTrack is null?"keine Sitzung":_playing?"Wiedergabe":"Pause")}\n"+
            $"Playback-Clock: {(_media.Clock.Valid?"gültig":"nicht verfügbar")}, "+
            $"Abweichung {_media.Clock.LastErrorMs:F1} ms\n"+
            $"Lyrics: {_lyricResult.Quality} · {_lyricResult.Source} · "+
            $"{_lines.Count} Zeilen\n"+
            $"Audio-Loopback: {(_audio.Available?"aktiv":"inaktiv")} · "+
            $"RMS {_audio.LastRms:F5}\n"+
            $"Vinyl-Renderaufruf: {Vinyl.LastRenderMs:F3} ms · "+
            $"{Vinyl.Quality}\n"+
            $"{timing}\n"+
            $"RAM: {current.WorkingSet64/1048576d:F1} MB · "+
            $"Monitore: {WinForms.Screen.AllScreens.Length}\n"+
            $"WPF-Zeiten sind keine GPU-Present-Zeitstempel.";
    }
    internal int SyncOffset(string scope)=>scope switch {
        "Global"=>_syncSettings.GlobalMs,
        "Gerät"=>_syncSettings.DeviceMs(_deviceKey),
        _=>_syncSettings.SongMs(_songKey)
    };
    internal void AdjustLyrics(string scope,int milliseconds)
    {
        switch(scope){
            case "Global":_syncSettings.AdjustGlobal(milliseconds);break;
            case "Gerät":_syncSettings.AdjustDevice(_deviceKey,milliseconds);break;
            default:_syncSettings.AdjustSong(_songKey,milliseconds);break;
        }
        _activeLyricIndex=-100;
        UpdateLyrics();
    }
    internal void ResetLyrics(string scope)
    {
        switch(scope){
            case "Global":_syncSettings.ResetGlobal();break;
            case "Gerät":_syncSettings.ResetDevice(_deviceKey);break;
            default:_syncSettings.ResetSong(_songKey);break;
        }
        _activeLyricIndex=-100;UpdateLyrics();
    }
    internal void SetLyricsAuto(bool enabled)
    {
        if(_settings.AutoLyricsSearch==enabled)return;
        ToggleLyricsAuto();
    }
    internal void SetPlainReader(bool enabled)
    {
        _settings.PlainLyricsReader=enabled;
        SettingsStore.Save(_settings);
        if(_lyricResult.Quality=="Plain"){
            if(enabled)ShowPlainLyrics();else ClearLyrics();
        }
    }
    internal void RescanLyrics()=>_=ManualSearchAsync();
    internal void SearchLyricVersions()=>_=SearchAlternativesAsync();
    internal void ImportLyrics()=>ImportOwnLrc();
    internal void SetClickThrough(bool enabled)
    {
        if(_settings.ClickThrough!=enabled)ToggleClickThrough();
    }
    internal void SetAutoStart(bool enabled)
    {
        if(_settings.AutoStart!=enabled)ToggleAutoStart();
    }
    internal void PrepareMove()
    {
        if(_settings.PositionLocked)return;
        _moveMode=true;Cursor=Cursors.SizeAll;
    }
    internal void MoveToMonitor(int index)
    {
        var screens=WinForms.Screen.AllScreens;
        if(index<0||index>=screens.Length)return;
        var area=screens[index].WorkingArea;
        Left=area.Left+(area.Width-Width)/2;
        Top=area.Top+(area.Height-Height)/2;
        SavePlacement();_desktop.QueuePlacement();
    }
    internal void ShowOverlayFromSettings()=>ShowOverlay();
    internal void HideOverlayFromSettings()=>HideOverlay();
    internal void ExitFromSettings()=>Close();
    internal void CopySyncDiagnostics()
    {
        try{
            System.Windows.Clipboard.SetText(
                $"Clock={_media.Clock.Estimated()?.TotalSeconds:F2}s; "+
                $"TimestampAge={_media.Clock.SampleAgeMs:F1}ms; "+
                $"ClockCorrection={_media.Clock.LastErrorMs:F1}ms; "+
                $"LyricsOffset={_syncSettings.EffectiveMs(_songKey,_deviceKey)}ms; "+
                $"Line={_activeLyricIndex}; Word={_karaoke.CurrentWordIndex}; "+
                $"LyricsProvider={_lyricResult.Source}; "+
                $"LyricsQuality={_lyricResult.Quality}; "+
                $"Lookup={_lyricsService.LastLookupMs:F1}ms");
        }catch(Exception ex){SettingsStore.Log(ex);}
    }
    private static async Task SafeMedia(Func<Task> command)
    {
        try { await command(); } catch(Exception ex) {SettingsStore.Log(ex);}
    }
    private void SetScale(double factor)
    {
        _settings.Scale=factor;Width=980*factor;Height=380*factor;SavePlacement();
    }
    private void ShowOverlay()
    {
        if (WindowState==WindowState.Minimized) WindowState=WindowState.Normal;
        Show();
        _settings.ClickThrough=false;
        UpdateWindowStyles();
        if (_playing && !_audio.Running) _audio.Start();
        StartFrameUpdates();
        _desktop.QueuePlacement();
    }
    private void HideOverlay()
    {
        _audio.Stop();
        StopFrameUpdates();
        Hide();
    }
    private void ToggleAutoStart()
    {
        try {
            using var reg=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (!_settings.AutoStart) {
                var executable=Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) ||
                    !executable.EndsWith("CharliVision.exe",StringComparison.OrdinalIgnoreCase)) return;
                reg?.SetValue("CharliVision", string.Concat((char)34, executable, (char)34));
            } else reg?.DeleteValue("CharliVision",false);
            _settings.AutoStart=!_settings.AutoStart;
            SavePlacement();
        } catch(Exception ex) {SettingsStore.Log(ex);}
    }
    private void ToggleClickThrough()
    {
        _settings.ClickThrough=!_settings.ClickThrough;UpdateWindowStyles();SavePlacement();
    }
    private void UpdateWindowStyles()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        if (hwnd==IntPtr.Zero) return;
        const int ExTransparent=0x20,ExToolWindow=0x80,ExLayered=0x80000,ExNoActivate=0x08000000;
        int flags=GetWindowLong(hwnd,-20);
        flags|=ExToolWindow|ExLayered|ExNoActivate;
        if (_settings.ClickThrough) flags|=ExTransparent;
        else flags &=~ExTransparent;
        SetWindowLong(hwnd,-20,flags);
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongW",SetLastError=true)]
    private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)]
    private static extern int SetWindowLong(IntPtr hwnd,int index,int value);

    private void SavePlacement()
    {
        if (_closing) return;
        _settings.Left=Left;_settings.Top=Top;
        SettingsStore.Save(_settings);
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        _closing=true;
        _settings.Left=Left;_settings.Top=Top;SettingsStore.Save(_settings);
        _glassMenu?.Close();
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        if(_chromaticSubscribed){
            CompositionTarget.Rendering-=ChromaticRender;
            _chromaticSubscribed=false;
        }
        _mediaTimer.Stop(); StopFrameUpdates();_audio.Dispose();
        _media.Dispose();
        _desktop.Dispose();
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsService.Dispose();
        _tray.Visible=false;_tray.Dispose();
    }
}