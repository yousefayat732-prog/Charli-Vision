using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CharliVision;

/// <summary>
/// Explicit opt-in WPF compositor test window, never used by normal startup.
/// Reports real UI callback intervals (not a GPU-present/frame-count claim).
/// </summary>
public static class FramePacingSmokeTest
{
    public static void RunBlocking(Application application,string mode)
    {
        var frame=new DispatcherFrame();
        Start(application,frame,mode);
        Dispatcher.PushFrame(frame);
    }
    private static void Start(Application application,DispatcherFrame frame,
        string mode)
    {
        var log=new List<string>{
            "CHARLI VISION — WPF FRAME PACING MEASUREMENT",
            DateTimeOffset.Now.ToString("u"),
            "Measurement = UI render callback spacing, not GPU present timestamps.",
            "Test uses a separate visible WPF window and deterministic test art."
        };
        var stats=new FramePacingStats();
        bool nativeMode=mode=="Native";
        log.Add("WPF_TIER="+(RenderCapability.Tier>>16));
        var compositorCallbacks=new FramePacingStats();
        var quality=mode=="Light"?VinylQuality.UltraLight:
            mode=="Balanced"?VinylQuality.Balanced:VinylQuality.Cinematic;
        log.Add("QUALITY="+quality);
        var vinyl=new VinylVisual {
            Width=350,Height=350,CenterTransparent=true,CoverRotates=true
        };
        vinyl.SetQuality(quality);
        if(mode=="Linear")RenderOptions.SetBitmapScalingMode(
            vinyl,BitmapScalingMode.Linear);
        vinyl.SetTrack(true,MakeTestArt(),true);
        UIElement display=vinyl;
        RotateTransform? nativeRotation=null;
        if(nativeMode){
            // Isolated native WPF transform comparator. This snapshot includes
            // the static spectrum, so it is NOT a drop-in renderer replacement.
            vinyl.Measure(new Size(350,350));
            vinyl.Arrange(new Rect(0,0,350,350));
            var frozenFrame=new RenderTargetBitmap(350,350,96,96,
                PixelFormats.Pbgra32);
            frozenFrame.Render(vinyl);frozenFrame.Freeze();
            nativeRotation=new RotateTransform();
            var nativeImage=new Image{
                Width=350,Height=350,Source=frozenFrame,
                RenderTransformOrigin=new Point(.5,.5),
                RenderTransform=nativeRotation,
                CacheMode=new BitmapCache()
            };
            display=nativeImage;
            log.Add("NATIVE_COMPARATOR=static full-disc bitmap; audio bars do not update");
        }
        var window=new Window{
            Title="CHARLI VISION - Vinyl Frame Diagnostics",
            Width=390,Height=390,ResizeMode=ResizeMode.NoResize,
            Background=new SolidColorBrush(Color.FromRgb(8,15,22)),
            Topmost=true,ShowInTaskbar=true,
            WindowStartupLocation=WindowStartupLocation.CenterScreen,
            Content=new Grid{Children={display}}
        };
        long first=Stopwatch.GetTimestamp(),prior=first;
        var cpuAtStart=Process.GetCurrentProcess().TotalProcessorTime;
        if(nativeMode && nativeRotation is not null){
            nativeRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0,12000,TimeSpan.FromSeconds(60)){
                    RepeatBehavior=RepeatBehavior.Forever
                },HandoffBehavior.SnapshotAndReplace);
        }
        var scheduler=new FramePacingScheduler();
        double targetMs=mode=="High"?1000d/180:
            mode=="Smooth"?1000d/120:1000d/60;
        long renderUpdates=0;
        bool done=false;
        EventHandler? callback=null;
        DispatcherTimer? timer=null;
        callback=(_,_)=>{
            if(done)return;
            long tick=Stopwatch.GetTimestamp();
            compositorCallbacks.AddFrame(tick,1000d/180);
            double now=(tick-first)/(double)Stopwatch.Frequency;
            if(nativeMode){
                stats.AddFrame(tick,1000d/180);
                renderUpdates++;
                return;
            }
            if(!scheduler.ShouldUpdate(now,targetMs))return;
            stats.AddFrame(tick,targetMs);
            double elapsed=(tick-prior)/(double)Stopwatch.Frequency;
            prior=tick;
            vinyl.Tick(elapsed,null);
            renderUpdates++;
        };
        var checkpoints=new List<string>();
        var monitor=new DispatcherTimer{Interval=TimeSpan.FromSeconds(10)};
        if(mode=="Long"){
            monitor.Tick+=(_,_)=>{
                var s=stats.GetSample();
                using var proc=Process.GetCurrentProcess();
                checkpoints.Add($"T+{(Stopwatch.GetTimestamp()-first)/
                    (double)Stopwatch.Frequency:F0}s"+
                    $" p95={s.P95Ms:F2}ms"+
                    $" RAM={proc.WorkingSet64/1048576d:F1}MB"+
                    $" missed={s.EstimatedMisses}");
            };
            monitor.Start();
        }
        timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(
            mode=="Long"?125:6)};
        timer.Tick+=(_,_)=>{
            if(done)return;
            done=true;
            timer.Stop();
            monitor.Stop();
            CompositionTarget.Rendering-=callback;
            if(mode=="Long")log.AddRange(checkpoints);
            var sample=stats.GetSample();
            var raw=compositorCallbacks.GetSample();
            log.Add("RAW_CALLBACKS="+raw.Samples);
            log.Add("RAW_CALLBACK_P50_MS="+raw.MedianMs.ToString("F2"));
            log.Add("RAW_CALLBACK_P95_MS="+raw.P95Ms.ToString("F2"));
            log.Add("RENDER_UPDATES="+renderUpdates);
            log.Add("MEASURED_UI_CALLBACKS="+sample.Samples);
            log.Add("MEAN_MS="+sample.MeanMs.ToString("F2"));
            log.Add("P50_MS="+sample.MedianMs.ToString("F2"));
            log.Add("P95_MS="+sample.P95Ms.ToString("F2"));
            log.Add("P99_MS="+sample.P99Ms.ToString("F2"));
            log.Add("MAX_MS="+sample.MaxMs.ToString("F2"));
            log.Add("ESTIMATED_BUDGET_MISSES="+sample.EstimatedMisses);
            log.Add("VINYL_ONRENDER_MS="+vinyl.LastRenderMs.ToString("F3"));
            var process=Process.GetCurrentProcess();
            log.Add("RAM_MB="+(process.WorkingSet64/1048576d).ToString("F1"));
            log.Add("CPU_ONE_CORE_PERCENT="+
                ((process.TotalProcessorTime-cpuAtStart).TotalSeconds/
                Math.Max(.1,(Stopwatch.GetTimestamp()-first)/
                (double)Stopwatch.Frequency)*100).ToString("F1"));
            bool success=sample.Samples>=25 && 
                (nativeMode || vinyl.LastRenderMs>0);
            try {
                window.UpdateLayout();
                var bitmap=new RenderTargetBitmap(350,350,96,96,PixelFormats.Pbgra32);
                bitmap.Render(display);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string output=Path.Combine(SettingsStore.DirectoryPath,
                    "ultimate-vinyl-frame-preview.png");
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                using(var file=File.Create(output))encoder.Save(file);
                log.Add("SCREENSHOT="+output);
            }catch(Exception ex){log.Add("SCREENSHOT_ERROR="+ex.Message);}
            log.Add("RESULT="+(success?"PASS":"INSUFFICIENT_VISIBLE_FRAMES"));
            try {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                    "ultimate-frame-test-"+mode+".txt"),log);
            }catch{}
            frame.Continue=false;
            window.Close();
            application.Shutdown(success?0:2);
        };
        window.Closed+=(_,_)=>{
            if(done)return;
            done=true;
            timer?.Stop();
            monitor.Stop();
            CompositionTarget.Rendering-=callback;
            log.Add("RESULT=CLOSED_EARLY");
            try{
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                    "ultimate-frame-test-"+mode+".txt"),log);
            }catch{}
            frame.Continue=false;
            application.Shutdown(2);
        };
        window.Show();
        window.Activate();
        CompositionTarget.Rendering+=callback;
        timer.Start();
    }
    private static BitmapSource MakeTestArt()
    {
        var canvas=new DrawingVisual();
        using(var dc=canvas.RenderOpen()){
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(12,38,65)),
                null,new Rect(0,0,256,256));
            var pen=new Pen(new SolidColorBrush(Color.FromRgb(65,211,225)),15);
            pen.Freeze();
            dc.DrawLine(pen,new Point(10,236),new Point(240,20));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(226,108,72)),
                null,new Point(128,128),60,60);
        }
        var image=new RenderTargetBitmap(256,256,96,96,PixelFormats.Pbgra32);
        image.Render(canvas);image.Freeze();
        return image;
    }
}
