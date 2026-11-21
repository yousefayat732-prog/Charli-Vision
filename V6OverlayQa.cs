using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharliVision;

/// <summary>
/// Explicit one-shot QA: real legal LRCLIB responses -> parser -> native WPF
/// text controls -> rendered images, without touching Spotify playback.
/// Never runs during regular application startup.
/// </summary>
internal static class V6OverlayQa
{
    private static readonly BindingFlags Fields=
        BindingFlags.Instance|BindingFlags.NonPublic;
    private static void Set(MainWindow host,string name,object? value)=>
        typeof(MainWindow).GetField(name,Fields)!.SetValue(host,value);
    private static void Invoke(MainWindow host,string name)=>
        typeof(MainWindow).GetMethod(name,Fields)!.Invoke(host,null);

    public static bool Run()
    {
        var report=new List<string>{
            "CHARLI VISION — V6 NATIVE WPF LYRICS E2E",
            DateTimeOffset.Now.ToString("u"),
            "Actual public LRCLIB transport and native WPF controls;",
            "test clock is synthetic, not a claim of real Spotify playback."
        };
        bool passed=true;
        try{
            using var source=new LyricsService();
            foreach(var entry in new[]{
                (Mode:"Word",Title:"Yellow",Artist:"Coldplay",
                    Album:"Parachutes",Seconds:0d),
                (Mode:"Line",Title:"Du hast",Artist:"Rammstein",
                    Album:"Sehnsucht",Seconds:0d),
                (Mode:"Plain",Title:"Hello There",Artist:"The Freshmen",
                    Album:"",Seconds:229d)
            }){
                try{
                    using var cts=new CancellationTokenSource(
                        TimeSpan.FromSeconds(20));
                    TimeSpan? duration=entry.Seconds>1?
                        TimeSpan.FromSeconds(entry.Seconds):null;
                    LyricResult response=Task.Run(()=>source.GetAsync(
                        entry.Title,entry.Artist,entry.Album,duration,cts.Token))
                        .GetAwaiter().GetResult();
                    if(response.Quality!=entry.Mode)
                        throw new InvalidOperationException(
                            "Expected "+entry.Mode+" but received "+response.Quality);
                    var host=new MainWindow();
                    Set(host,"_lyricResult",response);
                    Set(host,"_songKey",LyricsService.SongKey("QA "+entry.Mode,
                        "Native Renderer","",duration));
                    if(entry.Mode=="Plain"){
                        Set(host,"_plainLyrics",response.PlainText??"");
                        Invoke(host,"ShowPlainLyrics");
                        if(host.PlainLyricsReader.Visibility!=Visibility.Visible ||
                            string.IsNullOrEmpty(host.PlainLyricsText.Text))
                            throw new InvalidOperationException("Plain text hidden");
                    }else{
                        Set(host,"_lines",response.Lines);
                        int chosen=-1;
                        for(int i=1;i<response.Lines.Count-1;i++){
                            if(string.IsNullOrWhiteSpace(response.Lines[i-1].Text) ||
                                string.IsNullOrWhiteSpace(response.Lines[i].Text) ||
                                string.IsNullOrWhiteSpace(response.Lines[i+1].Text))
                                continue;
                            if(entry.Mode=="Word" &&
                                response.Lines[i].Words is not {Count:>0})continue;
                            chosen=i;break;
                        }
                        if(chosen<0)throw new InvalidOperationException(
                            "No complete three-line preview candidate");
                        var current=response.Lines[chosen];
                        TimeSpan at=entry.Mode=="Word"?
                            current.Words![0].Start+TimeSpan.FromMilliseconds(20):
                            current.Time+TimeSpan.FromMilliseconds(20);
                        TimeSpan end=TimeSpan.FromSeconds(
                            Math.Max(600,at.TotalSeconds+60));
                        var media=(MediaController)typeof(MainWindow).GetField(
                            "_media",Fields)!.GetValue(host)!;
                        var now=DateTimeOffset.UtcNow;
                        media.Clock.Update(new PlaybackClock(at,end,now,
                            false,true),1,PrecisionPlaybackClock.Stamp(),now,true);
                        Invoke(host,"UpdateLyrics");
                        if(host.PreviousLyric.Visibility!=Visibility.Visible ||
                            host.NextLyric.Visibility!=Visibility.Visible ||
                            string.IsNullOrWhiteSpace(host.PreviousLyric.Text) ||
                            string.IsNullOrWhiteSpace(host.NextLyric.Text))
                            throw new InvalidOperationException("3-line layout missing");
                        if(entry.Mode=="Word"){
                            if(host.WordLyricPanel.Visibility!=Visibility.Visible ||
                                host.WordLyricPanel.Children.Count==0)
                                throw new InvalidOperationException("Word panel hidden");
                        }else if(host.ActiveLyric.Visibility!=Visibility.Visible ||
                            string.IsNullOrWhiteSpace(host.ActiveLyric.Text))
                            throw new InvalidOperationException("Line panel hidden");
                    }
                    if(!host.LyricsRendererVisible)
                        throw new InvalidOperationException("No rendered WPF lyrics");
                    var visual=(FrameworkElement)host.Content;
                    visual.Measure(new Size(980,380));
                    visual.Arrange(new Rect(0,0,980,380));
                    visual.UpdateLayout();
                    var bitmap=new RenderTargetBitmap(980,380,96,96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder=new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path=Path.Combine(SettingsStore.DirectoryPath,
                        "v6-qa-"+entry.Mode.ToLowerInvariant()+".png");
                    using(var file=File.Create(path))encoder.Save(file);
                    report.Add("PASS: "+entry.Mode+
                        " online provider -> native visible WPF -> PNG, cues="+
                        response.Lines.Count+" bytes="+new FileInfo(path).Length);
                }catch(Exception ex){
                    passed=false;
                    report.Add("FAIL: "+entry.Mode+" "+ex.GetType().Name+
                        " "+ex.Message);
                }
            }
        }catch(Exception ex){
            passed=false;
            report.Add("FAIL: "+ex.GetType().Name+" "+ex.Message);
        }
        report.Add("RESULT="+(passed?"PASS":"FAIL"));
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
            "v6-overlay-e2e.txt"),report);
        return passed;
    }
}
