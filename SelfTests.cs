using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharliVision;

public static class SelfTests
{
    private sealed class StubLyricsHttp:System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage,CancellationToken,
            System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>> _handle;
        public int Calls;
        public StubLyricsHttp(Func<System.Net.Http.HttpRequestMessage,CancellationToken,
            System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>> handle)
        { _handle=handle; }
        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage>
            SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            return await _handle(request,ct);
        }
        public static System.Net.Http.HttpResponseMessage Json(string json)
        {
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){
                Content=new System.Net.Http.StringContent(json,System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
    public static bool Run()
    {
        var report=new List<string>{"Charli Vision Chromatic Lyrics self-tests (no user music data): "
            +DateTimeOffset.Now.ToString("u")};
        int failed=0;
        void Check(string name,Func<bool> test) {
            try {
                bool okay=test();
                report.Add((okay?"PASS: ":"FAIL: ")+name);
                if(!okay)failed++;
            } catch(Exception ex){
                failed++;report.Add("FAIL: "+name+" ("+ex.GetType().Name+": "+ex.Message+")");
            }
        }
        var lines=LyricsService.ParseLrc(
            "[ar:Example Artist]\n[00:01.00]Original sample line A\n"+
            "[00:02.25]Original sample line B\n[00:04.00]\n");
        Check("LRC parser: correctly timed 3 cues",() =>
            lines.Count==3 && lines[0].Time==TimeSpan.FromSeconds(1) &&
            lines[1].Time==TimeSpan.FromMilliseconds(2250));
        Check("LRC active line binary search",() =>
            LyricsService.ActiveAt(lines,TimeSpan.FromMilliseconds(1100))==0 &&
            LyricsService.ActiveAt(lines,TimeSpan.FromMilliseconds(2800))==1 &&
            LyricsService.ActiveAt(lines,TimeSpan.FromMilliseconds(50))==-1);
        Check("Universal: Enhanced LRC retains actual word starts",()=>{
            var enhanced=LyricsService.ParseLrc(
                "[00:01.00]<00:01.10>Hello <00:01.60>future");
            if(enhanced.Count!=1||enhanced[0].Text!="Hello future"||
                enhanced[0].Words is not {Count:2} words)return false;
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel);
            if(!renderer.Setup(enhanced[0]))return false;
            renderer.Update(TimeSpan.FromMilliseconds(1700));
            return words[0].Start==TimeSpan.FromMilliseconds(1100) &&
                words[0].End is null &&
                words[1].Start==TimeSpan.FromMilliseconds(1600) &&
                renderer.CurrentWordIndex==1;
        });
        Check("Universal: malformed Enhanced LRC rejects wrong word times",()=>{
            var wrong=LyricsService.ParseLrc(
                "[00:04.00]Unmarked <00:05.00>partial <00:03.00>earlier");
            return wrong.Count==1 && wrong[0].Words is null &&
                wrong[0].Text=="Unmarked partial earlier";
        });
        Check("Universal: exported Enhanced LRC reimports as Word quality",()=>{
            string root=Path.Combine(Path.GetTempPath(),"CVEnhanced-"+
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try{
                var library=new LyricsLibrary(root);
                var preview=new LocalAlignmentService.Preview("demo.wav",
                    "demo.txt","en",5000,1,2,2,Array.Empty<int>(),new[]{
                    new LocalAlignmentService.Line("Hello future",1200,new[]{
                        new LocalAlignmentService.Word("Hello ",1200,1800,.98),
                        new LocalAlignmentService.Word("future",1800,2600,.92)})
                });
                var key=LyricsService.SongKey("Own Enhanced","Own Artist",
                    "Album",TimeSpan.FromSeconds(5));
                library.Import(key,preview.ToEnhancedLrc());
                using var service=new LyricsService(null,library);
                var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                    "Own Enhanced","Own Artist","Album",TimeSpan.FromSeconds(5),
                    CancellationToken.None)).GetAwaiter().GetResult();
                return result.Quality=="Word"&&result.Synchronized &&
                    result.Lines[0].Words is {Count:2};
            }finally{try{Directory.Delete(root,true);}catch{}}
        });
        Check("Instrumental break cue keeps empty content",() =>
            LyricsService.ActiveAt(lines,TimeSpan.FromSeconds(4))==2 && lines[2].Text=="");
        Check("Invalid LRC metadata is not displayed",() =>
            LyricsService.ParseLrc("[ar:Ignored]\n[not-a-time]not a cue").Count==0);
        Check("Offset calibration",()=>
            LyricsService.ParseLrc("[offset:250]\n[00:01.00]sample")[0].Time ==
            TimeSpan.FromMilliseconds(1250));
        Check("Multiple time tags share one lyric text",()=> {
            var repeated=LyricsService.ParseLrc("[00:01.00][00:02.00]Original cue");
            return repeated.Count==2 && repeated.All(line=>line.Text=="Original cue");
        });
        var originalYaml=string.Join('\n',new[]{
            "version: '1.0'",
            "metadata:",
            "  title: 'Original sample'",
            "  artist: 'Test'",
            "lines:",
            "  - text: 'Hello future'",
            "    start_ms: 1200",
            "    words:",
            "      - text: 'Hello '",
            "        start_ms: 1200",
            "        end_ms: 1800",
            "      - text: 'future'",
            "        start_ms: 1800",
            "        end_ms: 2600"
        });
        var timedWords=WordLyricsParser.ParseLyricsfile(originalYaml);
        Check("Lyricsfile real word stamps parsed",()=>timedWords.Count==1 &&
            timedWords[0].Words is {Count:2} parsed &&
            parsed[0].Start==TimeSpan.FromMilliseconds(1200) &&
            parsed[0].End==TimeSpan.FromMilliseconds(1800));
        Check("Word end times come from source",()=>timedWords.Count==1 &&
            timedWords[0].Words?[1].End==TimeSpan.FromMilliseconds(2600));
        Check("Invalid Lyricsfile version rejected",()=>
            WordLyricsParser.ParseLyricsfile(originalYaml.Replace("version: '1.0'",
                "version: '99'")).Count==0);
        Check("Word mismatch cannot cause false highlighting",()=>
            WordLyricsParser.ParseLyricsfile(originalYaml.Replace("'Hello future'",
                "'Different words'"))[0].Words is null);
        Check("Line-only Lyricsfile has no invented word timestamps",()=>
            WordLyricsParser.ParseLyricsfile(string.Join('\n',new[]{
                "version: '1.0'","metadata:","  title: 'Test'","  artist: 'Test'",
                "lines:","  - text: 'Line only'","    start_ms: 1000"
            }))[0].Words is null);
        Check("Unavailable lyrics contain no visible text data",()=>
            LyricResult.Unavailable("not found").Lines.Count==0);
        Check("WPF karaoke region is blank by default and after clear",()=> {
            var window=new MainWindow();
            bool initiallyHidden=window.TitleRegion.Visibility==System.Windows.Visibility.Collapsed &&
                window.ActiveLyric.Text=="" && window.PreviousLyric.Text=="" &&
                window.NextLyric.Text=="";
            var clear=typeof(MainWindow).GetMethod("ClearLyrics",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            clear?.Invoke(window,new object[]{true});
            return initiallyHidden && window.TitleRegion.Visibility==System.Windows.Visibility.Collapsed &&
                window.ActiveLyric.Text=="" && window.PreviousLyric.Text=="" &&
                window.NextLyric.Text=="";
        });
        Check("Seek backwards restores upcoming-word colors",()=> {
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel);
            var words=new [] {
                new LyricWord(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),"Hello "),
                new LyricWord(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(3),"future")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),"Hello future",words)))return false;
            renderer.Update(TimeSpan.FromSeconds(4));
            var second=(System.Windows.Controls.TextBlock)panel.Children[1];
            var completedBrush=second.Foreground;
            renderer.Update(TimeSpan.FromMilliseconds(400));
            var reverted=second.Foreground!=completedBrush;
            renderer.Clear();
            return reverted;
        });
        var red=AlbumPalette.Analyze(Sample(245,50,55));
        var blue=AlbumPalette.Analyze(Sample(33,70,250));
        var gray=AlbumPalette.Analyze(Sample(130,130,130));
        Check("Distinct album colors red/blue",()=>red.Accent.R>red.Accent.B&&
            blue.Accent.B>blue.Accent.R);
        Check("Monochrome cover produces neutral palette",()=>gray.Monochrome);
        Check("Crossfade endpoints",()=>AlbumPalette.Lerp(red,blue,0)==red&&
            AlbumPalette.Lerp(red,blue,1)==blue);
        var paused=new PlaybackClock(TimeSpan.FromSeconds(18),
            TimeSpan.FromSeconds(190),DateTimeOffset.UtcNow.AddSeconds(-9),false,true);
        var running=new PlaybackClock(TimeSpan.FromSeconds(18),
            TimeSpan.FromSeconds(190),DateTimeOffset.UtcNow.AddSeconds(-2),true,true);
        Check("Paused timeline does not advance",()=>paused.Estimated()==TimeSpan.FromSeconds(18));
        Check("Playing timeline moves forward from Windows last-update",()=>
            running.Estimated()>TimeSpan.FromSeconds(19)&&running.Estimated()<TimeSpan.FromSeconds(24));
        Check("Invalid timeline never fabricates position",()=>
            new PlaybackClock(TimeSpan.Zero,TimeSpan.Zero,DateTimeOffset.MinValue,true,false).Estimated()==null);

        Check("Precision clock: monotonic time despite changing wall clock",()=> {
            var sync=new PrecisionPlaybackClock();
            long stamp=PrecisionPlaybackClock.Stamp();
            var now=DateTimeOffset.UtcNow;
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(12),TimeSpan.FromSeconds(180),
                now.AddSeconds(-1),true,true),1,stamp,now,true);
            var after=sync.Estimated(stamp+System.Diagnostics.Stopwatch.Frequency/2);
            return after is not null && Math.Abs(after.Value.TotalSeconds-13.5)<.025;
        });
        Check("Precision clock: pause freezes and seek resets instantly",()=> {
            var sync=new PrecisionPlaybackClock();
            long stamp=PrecisionPlaybackClock.Stamp();
            var now=DateTimeOffset.UtcNow;
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(12),TimeSpan.FromSeconds(180),
                now,true,true),1,stamp,now,true);
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(180),
                now,false,true),1,stamp+System.Diagnostics.Stopwatch.Frequency,now,true);
            var frozen=sync.Estimated(stamp+10*System.Diagnostics.Stopwatch.Frequency);
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(180),
                now,true,true),1,stamp+11*System.Diagnostics.Stopwatch.Frequency,now,true);
            var seek=sync.Estimated(stamp+11*System.Diagnostics.Stopwatch.Frequency);
            return frozen==TimeSpan.FromSeconds(15) &&
                seek==TimeSpan.FromSeconds(60);
        });
        Check("Precision clock: playback rate is respected",()=> {
            var sync=new PrecisionPlaybackClock();
            long stamp=PrecisionPlaybackClock.Stamp();
            var now=DateTimeOffset.UtcNow;
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(8),TimeSpan.FromSeconds(150),
                now,true,true),1.5,stamp,now,true);
            var future=sync.Estimated(stamp+2*System.Diagnostics.Stopwatch.Frequency);
            return future==TimeSpan.FromSeconds(11);
        });
        Check("Precision clock: stale timeline falls back without false backjump",()=> {
            var sync=new PrecisionPlaybackClock();
            long stamp=PrecisionPlaybackClock.Stamp();
            var now=DateTimeOffset.UtcNow;
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(22),TimeSpan.FromSeconds(180),
                now,true,true),1,stamp,now,true);
            sync.Update(new PlaybackClock(TimeSpan.FromSeconds(22),TimeSpan.FromSeconds(180),
                now.AddMinutes(-5),true,true),1,stamp+3*System.Diagnostics.Stopwatch.Frequency,
                now.AddSeconds(3));
            var predicted=sync.Estimated(stamp+3*System.Diagnostics.Stopwatch.Frequency);
            return predicted is not null && Math.Abs(predicted.Value.TotalSeconds-25)<.03;
        });
        Check("Lyrics sync: song offsets persist without modifying user settings",()=> {
            string temporary=Path.Combine(Path.GetTempPath(),
                "CVSync-Test-"+Guid.NewGuid().ToString("N")+".json");
            try {
                var key=LyricsSyncSettings.SongKey("Example","Artist","Album",
                    TimeSpan.FromSeconds(180));
                var calibration=new LyricsSyncSettings(temporary);
                calibration.AdjustGlobal(100);calibration.AdjustSong(key,350);
                var reread=new LyricsSyncSettings(temporary);
                return reread.EffectiveMs(key,null)==450 &&
                    reread.Apply(TimeSpan.FromSeconds(10),key,null)==
                        TimeSpan.FromMilliseconds(10450);
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        });
        Check("Karaoke gradient: active word uses real-progress sweep",()=> {
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel);
            var words=new []{
                new LyricWord(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(3),"Hello "),
                new LyricWord(TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(4),"world")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),"Hello world",words)))return false;
            renderer.Update(TimeSpan.FromMilliseconds(1200));
            var block=(System.Windows.Controls.TextBlock)panel.Children[0];
            var gradient=block.Foreground as System.Windows.Media.LinearGradientBrush;
            if(gradient is null)return false;
            var first=gradient.GradientStops[1].Offset;
            renderer.Update(TimeSpan.FromMilliseconds(2200));
            bool advanced=gradient.GradientStops[1].Offset>first+.15;
            renderer.ReducedMotion=true;
            renderer.Update(TimeSpan.FromMilliseconds(2400));
            var transform=(System.Windows.Media.ScaleTransform)block.RenderTransform;
            return advanced && renderer.CurrentWordIndex==0 &&
                Math.Abs(transform.ScaleX-1)<.001;
        });

        Check("Cover: 512px circular texture is frozen and reused",()=>{
            var src=new DrawingVisual();
            using(var dc=src.RenderOpen())
                dc.DrawRectangle(Brushes.CornflowerBlue,null,
                    new System.Windows.Rect(0,0,240,160));
            var art=new RenderTargetBitmap(240,160,96,96,PixelFormats.Pbgra32);
            art.Render(src);art.Freeze();
            var disc=new VinylVisual{CoverRotates=true};
            disc.SetTrack(true,art,true);
            if(!disc.CachedCoverFrozen || disc.CachedCoverPixelWidth!=512)
                return false;
            disc.Tick(.3,null);
            return disc.CachedCoverFrozen && disc.CachedCoverPixelWidth==512 &&
                disc.RotationDegrees>0;
        });
        Check("Vinyl material cached and frozen",()=>{
            var disc=new VinylVisual();
            return disc.CachedMaterialFrozen && disc.GrooveCount>45 &&
                disc.Quality==VinylQuality.Balanced;
        });
        Check("Vinyl quality changes cached groove density",()=>{
            var disc=new VinylVisual();
            int normal=disc.GrooveCount;
            disc.SetQuality(VinylQuality.UltraLight);
            int light=disc.GrooveCount;
            disc.SetQuality(VinylQuality.Cinematic);
            return disc.CachedMaterialFrozen && light<normal &&
                disc.GrooveCount>=normal;
        });
        Check("Audio attack and release use actual bounded data",()=>{
            var disc=new VinylVisual();
            var levels=new float[92];levels[15]=1;
            disc.SetTrack(true,null,false);
            disc.Tick(.016,levels);
            float attack=disc.SpectrumValue(15);
            disc.Tick(.016,levels);
            float later=disc.SpectrumValue(15);
            disc.SetTrack(false,null,false);
            disc.Tick(.016,null);
            float release=disc.SpectrumValue(15);
            return attack>0 && attack<later && later<1 &&
                release<later && release>0;
        });
        Check("Vinyl rotation is time-based and slows on pause",()=>{
            var disc=new VinylVisual();
            disc.SetTrack(true,null,false);
            for(int i=0;i<120;i++)disc.Tick(1d/60,null);
            double moving=disc.RotationSpeed;
            disc.SetTrack(false,null,false);
            for(int i=0;i<120;i++)disc.Tick(1d/60,null);
            return moving>180 && moving<201 && disc.RotationSpeed<moving*.08;
        });
        Check("Ultimate P1: active word keeps timestamp and smooth opacity",()=>{
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel);
            var words=new[]{
                new LyricWord(TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1.6),"Hello "),
                new LyricWord(TimeSpan.FromSeconds(1.6),
                    TimeSpan.FromSeconds(2.4),"future")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),
                "Hello future",words)))return false;
            renderer.Update(TimeSpan.FromMilliseconds(1500));
            var first=(System.Windows.Controls.TextBlock)panel.Children[0];
            bool beginning=renderer.CurrentWordIndex==0&&
                first.Effect is System.Windows.Media.Effects.DropShadowEffect;
            renderer.Update(TimeSpan.FromMilliseconds(1700));
            var second=(System.Windows.Controls.TextBlock)panel.Children[1];
            return beginning&&renderer.CurrentWordIndex==1 &&
                second.Effect is System.Windows.Media.Effects.DropShadowEffect &&
                first.Effect is null && second.RenderTransform is
                    System.Windows.Media.ScaleTransform;
        });
        Check("Ultimate P1: line entrance uses inexpensive WPF transforms",()=>{
            var enter=typeof(MainWindow).GetMethod("AnimateLyricEntrance",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Static);
            var element=new System.Windows.Controls.TextBlock();
            enter?.Invoke(null,new object[]{element,6d,1d,90d});
            return enter is not null &&
                element.RenderTransform is System.Windows.Media.TranslateTransform &&
                element.Opacity>0 && element.Opacity<=1;
        });
        Check("Ultimate accessibility: high-contrast pending lyrics stay readable",()=>{
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel){HighContrast=true};
            renderer.ApplyTheme(AlbumPalette.Neutral);
            var words=new[]{
                new LyricWord(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),"Hello "),
                new LyricWord(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(3),"world")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),
                "Hello world",words)))return false;
            renderer.Update(TimeSpan.FromMilliseconds(1500));
            var pending=(System.Windows.Controls.TextBlock)panel.Children[1];
            var brush=pending.Foreground as System.Windows.Media.SolidColorBrush;
            return brush is not null && brush.Color.R>=220 &&
                pending.Opacity>=.85 && renderer.CurrentWordIndex==0;
        });
        Check("Ultimate P3: lyrics editor YAML keeps real word end timestamps",()=>{
            var data=new LyricResult(new[]{
                new LyricLine(TimeSpan.FromSeconds(1),"Hello future",
                    new[]{
                        new LyricWord(TimeSpan.FromMilliseconds(1000),
                            TimeSpan.FromMilliseconds(1580),"Hello "),
                        new LyricWord(TimeSpan.FromMilliseconds(1580),
                            TimeSpan.FromMilliseconds(2390),"future")
                    })
            },"own",true,"Test","Word");
            string text=LyricsEditorDocument.Export(data);
            var parsed=LyricsEditorDocument.Validate(text,"Word");
            return parsed.Lines.Count==1&&parsed.Lines[0].Words is {Count:2} &&
                parsed.Lines[0].Words![1].End==TimeSpan.FromMilliseconds(2390);
        });
        Check("Ultimate P3: editor time shifts are explicit and reversible",()=>{
            string original="[00:01.00]Original line";
            string moved=LyricsEditorDocument.ShiftCurrentTimestamp(
                original,5,50);
            string back=LyricsEditorDocument.ShiftCurrentTimestamp(moved,5,-50);
            string yaml="      - start_ms: 1000";
            string word=LyricsEditorDocument.ShiftCurrentTimestamp(yaml,10,50);
            return moved.StartsWith("[00:01.05]")&&back==original &&
                word.Contains("1050")&&
                LyricsEditorDocument.Validate(moved,"Line").Lines[0].Time==
                    TimeSpan.FromMilliseconds(1050);
        });
        Check("Ultimate P3: plain text stays unsynchronized after edit",()=>{
            var result=LyricsEditorDocument.Validate(
                "First stanza\nSecond stanza","Plain");
            return result.Lines.Count==0&&!result.Synchronized &&
                result.PlainText=="First stanza\nSecond stanza";
        });
        Check("Ultimate P3: editor WPF layout initializes without media",()=>{
            var host=new MainWindow();
            var editor=new LyricsEditorWindow(host,"0123456789ABCDEF01234567",
                LyricResult.Unavailable("Kein Text"));
            return editor.Content is System.Windows.Controls.Grid &&
                editor.Title.Contains("Lyrics") && editor.Width>=700;
        });
        Check("Ultimate P3: lyrics editor renders actual WPF controls",()=>{
            var host=new MainWindow();
            var example=new LyricResult(new[]{
                new LyricLine(TimeSpan.FromSeconds(1),"Hello future",
                    new[]{new LyricWord(TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),"Hello "),
                        new LyricWord(TimeSpan.FromSeconds(2),
                            TimeSpan.FromSeconds(3),"future")})
            },"Local QA",true,"Own Test","Word");
            var editor=new LyricsEditorWindow(host,"0123456789ABCDEF01234567",example);
            if(editor.Content is not System.Windows.Controls.Grid layout)return false;
            layout.Measure(new System.Windows.Size(830,660));
            layout.Arrange(new System.Windows.Rect(0,0,830,660));
            layout.UpdateLayout();
            var bitmap=new RenderTargetBitmap(830,660,96,96,PixelFormats.Pbgra32);
            bitmap.Render(layout);
            var output=Path.Combine(SettingsStore.DirectoryPath,
                "ultimate-editor-layout.png");
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(output))encoder.Save(file);
            return bitmap.PixelWidth==830&&File.Exists(output);
        });
        Check("Ultimate P3: editor preview follows exact word timestamps",()=>{
            var host=new MainWindow();
            var own=new LyricResult(new[]{
                new LyricLine(TimeSpan.FromMilliseconds(1000),"Hello future",
                    new[]{
                        new LyricWord(TimeSpan.FromMilliseconds(1000),
                            TimeSpan.FromMilliseconds(1600),"Hello "),
                        new LyricWord(TimeSpan.FromMilliseconds(1600),
                            TimeSpan.FromMilliseconds(2300),"future")
                    })
            },"Own demo",true,"Test","Word");
            var editor=new LyricsEditorWindow(host,
                "0123456789ABCDEF01234567",own);
            var start=typeof(LyricsEditorWindow).GetMethod("LoadPreview",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance);
            start?.Invoke(editor,null);
            var field=typeof(LyricsEditorWindow).GetField("_karaoke",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance);
            var seekField=typeof(LyricsEditorWindow).GetField("_seek",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance);
            var karaoke=field?.GetValue(editor) as KaraokeWords;
            var seek=seekField?.GetValue(editor) as System.Windows.Controls.Slider;
            if(karaoke is null||seek is null||karaoke.CurrentWordIndex!=0)
                return false;
            seek.Value=1800;
            return karaoke.CurrentWordIndex==1;
        });
        Check("ZeroStutter: 60/120/180Hz never change vinyl angular velocity",()=>{
            var a=new VinylVisual();
            var b=new VinylVisual();
            var c=new VinylVisual();
            foreach(var v in new[]{a,b,c})v.SetTrack(true,null,false);
            for(int i=0;i<600;i++)a.Tick(1d/60,null);
            for(int i=0;i<1200;i++)b.Tick(1d/120,null);
            for(int i=0;i<1800;i++)c.Tick(1d/180,null);
            return Math.Abs(a.RotationDegrees-b.RotationDegrees)<.001 &&
                Math.Abs(b.RotationDegrees-c.RotationDegrees)<.001 &&
                Math.Abs(a.RotationSpeed-b.RotationSpeed)<.001 &&
                Math.Abs(b.RotationSpeed-c.RotationSpeed)<.001;
        });
        Check("ZeroStutter: high-refresh scheduler is frame-rate independent",()=>{
            var high=new FramePacingScheduler();
            var normal=new FramePacingScheduler();
            int highUpdates=0,normalUpdates=0;
            for(int i=0;i<=1800;i++){
                double at=1+i/180d;
                if(high.ShouldUpdate(at,1000d/180))highUpdates++;
                if(normal.ShouldUpdate(at,1000d/60))normalUpdates++;
            }
            return highUpdates>1550 && highUpdates<=1801 &&
                normalUpdates>=560 && normalUpdates<=650 &&
                highUpdates>normalUpdates*2;
        });
        Check("ZeroStutter: long monotonic gap loses no record rotation",()=>{
            var continuous=new VinylVisual();
            var jumped=new VinylVisual();
            continuous.SetTrack(true,null,false);
            jumped.SetTrack(true,null,false);
            for(int i=0;i<180;i++)continuous.Tick(1,null);
            jumped.Tick(180,null);
            return Math.Abs(continuous.RotationDegrees-jumped.RotationDegrees)<.001 &&
                Math.Abs(continuous.RotationSpeed-jumped.RotationSpeed)<.001;
        });
        Check("Ultimate P0: analytic vinyl rotation is invariant to frame drops",()=>{
            var smooth=new VinylVisual();
            var bursty=new VinylVisual();
            smooth.SetTrack(true,null,false);
            bursty.SetTrack(true,null,false);
            for(int i=0;i<120;i++)smooth.Tick(1d/60,null);
            bursty.Tick(.45,null);
            bursty.Tick(.75,null);
            bursty.Tick(.80,null);
            double angularDifference=Math.Abs(smooth.RotationDegrees-bursty.RotationDegrees);
            double speedDifference=Math.Abs(smooth.RotationSpeed-bursty.RotationSpeed);
            return angularDifference<.0005 && speedDifference<.0005;
        });
        Check("Ultimate P0: paused vinyl retains angle while easing to stop",()=>{
            var disc=new VinylVisual();
            disc.SetTrack(true,null,false);
            disc.Tick(3,null);
            double before=disc.RotationDegrees;
            disc.SetTrack(false,null,false);
            disc.Tick(1.2,null);
            var slowing=disc.RotationSpeed;
            disc.Tick(3,null);
            return before>=0&&before<360&&slowing>0&&slowing<40 &&
                disc.RotationSpeed<.3&&disc.RotationDegrees>=0 &&
                disc.RotationDegrees<360;
        });
        Check("Ultimate P0: anchored scheduler avoids extra wait after late frame",()=>{
            var anchored=new FramePacingScheduler();
            const double budget=16.67;
            var events=new double[]{
                1.000,1.0056,1.0112,1.0168,1.0224,1.0280,
                1.0550,1.0606,1.0662,1.0718,1.0774,1.0830
            };
            int drawn=0;
            double naiveLast=0;
            int naiveDrawn=0;
            foreach(double at in events){
                if(anchored.ShouldUpdate(at,budget))drawn++;
                if(naiveLast==0||(at-naiveLast)*1000>=budget-1){
                    naiveDrawn++;naiveLast=at;
                }
            }
            return drawn>=naiveDrawn && drawn<=events.Length &&
                !anchored.ShouldUpdate(double.NaN,budget);
        });
        Check("Zero Stutter: early compositor callbacks do not skip 60Hz frames",()=>{
            var scheduler=new FramePacingScheduler();
            double[] jitter={0,-.0014,.0003,-.0012,0};
            int rendered=0;
            for(int i=0;i<180;i++){
                double now=1+i*.01667+jitter[i%jitter.Length];
                if(scheduler.ShouldUpdate(now,16.67))rendered++;
            }
            return rendered==180;
        });
        Check("Ultimate P0: bounded frame pacing histogram percentiles",()=>{
            var pacing=new FramePacingStats();
            long timestamp=System.Diagnostics.Stopwatch.GetTimestamp();
            pacing.AddFrame(timestamp,16.67);
            for(int i=0;i<120;i++){
                timestamp+=(long)(System.Diagnostics.Stopwatch.Frequency*
                    (i==75?.045:.0167));
                pacing.AddFrame(timestamp,16.67);
            }
            var sample=pacing.GetSample();
            return sample.Samples==120 && sample.P95Ms>15 &&
                sample.P95Ms<18 && sample.MaxMs>40 &&
                sample.EstimatedMisses>=1;
        });
        Check("Ultimate P0: audio spectrum snapshot reuses caller buffer",()=>{
            using var engine=new AudioEngine();
            var copy=new float[92];
            engine.SnapshotInto(copy);
            engine.SnapshotInto(copy);
            return copy.Length==92 && copy.All(x=>x==0);
        });
        Check("Vinyl renderer quality renders with WPF",()=>{
            // The legacy opacity assertion tests the optional dark center mode.
            var disc=new VinylVisual{CenterTransparent=false};
            disc.Measure(new System.Windows.Size(350,350));
            disc.Arrange(new System.Windows.Rect(0,0,350,350));
            var levels=new float[92];levels[15]=.8f;
            disc.SetTrack(true,null,false);
            disc.Tick(.016,levels);
            foreach(var q in new[]{VinylQuality.UltraLight,VinylQuality.Balanced,
                VinylQuality.Cinematic}){
                disc.SetQuality(q);
                var bitmap=new RenderTargetBitmap(350,350,96,96,PixelFormats.Pbgra32);
                bitmap.Render(disc);
                if(disc.LastRenderMs<=0)return false;
                byte[] center=new byte[4];
                bitmap.CopyPixels(new System.Windows.Int32Rect(175,175,1,1),center,4,0);
                if(center[3]<180)return false;
            }
            return true;
        });

        Check("Lyrics version matching rejects live/remix substitutions",()=>{
            using var regular=System.Text.Json.JsonDocument.Parse(
                "{\"trackName\":\"Original Echo\",\"artistName\":\"Test Singer\",\"duration\":200}");
            using var remix=System.Text.Json.JsonDocument.Parse(
                "{\"trackName\":\"Original Echo (Remix)\",\"artistName\":\"Test Singer\",\"duration\":200}");
            return !LyricsService.MetadataMatches(regular.RootElement,
                "Original Echo (Live)","Test Singer","",TimeSpan.FromSeconds(200)) &&
                !LyricsService.MetadataMatches(remix.RootElement,
                "Original Echo","Test Singer","",TimeSpan.FromSeconds(200)) &&
                LyricsService.MetadataMatches(regular.RootElement,
                "Original Echo","Test Singer","",TimeSpan.FromSeconds(200));
        });
        Check("Lyrics duration and primary artist reject wrong recording",()=>{
            using var different=System.Text.Json.JsonDocument.Parse(
                "{\"trackName\":\"Original Echo\",\"artistName\":\"Another Singer\",\"duration\":202}");
            using var duration=System.Text.Json.JsonDocument.Parse(
                "{\"trackName\":\"Original Echo\",\"artistName\":\"Test Singer\",\"duration\":225}");
            return !LyricsService.MetadataMatches(different.RootElement,
                "Original Echo","Test Singer","",TimeSpan.FromSeconds(202)) &&
                !LyricsService.MetadataMatches(duration.RootElement,
                "Original Echo","Test Singer","",TimeSpan.FromSeconds(202));
        });
        Check("Lyrics candidates prefer synchronized exact album recording",()=>{
            using var json=System.Text.Json.JsonDocument.Parse(
                "[{\"id\":11,\"trackName\":\"Original Echo\",\"artistName\":\"Test Singer\","+
                "\"albumName\":\"Wrong Album\",\"duration\":201,\"syncedLyrics\":\"[00:01.00]Sample\"},"+
                "{\"id\":12,\"trackName\":\"Original Echo\",\"artistName\":\"Test Singer\","+
                "\"albumName\":\"Original Album\",\"duration\":200,\"syncedLyrics\":\"[00:01.00]Sample\"},"+
                "{\"id\":13,\"trackName\":\"Original Echo (Live)\",\"artistName\":\"Test Singer\","+
                "\"albumName\":\"Original Album\",\"duration\":200,\"syncedLyrics\":\"[00:01.00]Sample\"}]");
            var ranked=LyricsService.RankAlternatives(json.RootElement,
                "Original Echo","Test Singer","Original Album",TimeSpan.FromSeconds(200));
            return ranked.Count==2&&ranked[0].Id==12&&ranked[1].Id==11;
        });
        Check("Lyrics import and manual id choices persist locally",()=>{
            string dir=Path.Combine(Path.GetTempPath(),"CVLyricsTest-"+Guid.NewGuid().ToString("N"));
            try{
                var a=new LyricsLibrary(dir);
                var key=LyricsService.SongKey("Own Sample","Author","Own Album",
                    TimeSpan.FromSeconds(145));
                a.Select(key,123);
                a.Import(key,"[00:01.00]Original example line");
                var b=new LyricsLibrary(dir);
                return b.SelectedId(key)==123 && b.HasImport(key) &&
                    LyricsService.ParseLrc(b.GetImport(key)??"").Count==1;
            }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        });
        Check("Universal: broad LRCLIB query rescues missing metadata search",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                string path=req.RequestUri!.AbsolutePath;
                string query=req.RequestUri.Query;
                if(path=="/api/get")
                    return System.Threading.Tasks.Task.FromResult(
                        new System.Net.Http.HttpResponseMessage(
                            System.Net.HttpStatusCode.NotFound));
                if(path=="/api/search" && query.Contains("q=",StringComparison.Ordinal))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "[{\"id\":506,\"trackName\":\"Broad Original\","+
                        "\"artistName\":\"Own Artist\",\"albumName\":\"Album\","+
                        "\"duration\":192,\"syncedLyrics\":\"[00:02.00]Found real line\"}]"));
                return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Broad Original","Own Artist","Album",TimeSpan.FromSeconds(192),
                CancellationToken.None)).GetAwaiter().GetResult();
            return result.Quality=="Line"&&result.Lines.Count==1&&stub.Calls==3;
        });
        Check("Universal: timed upgrade uses same WPF karaoke renderer",()=>{
            string safeYaml=originalYaml;
            string word=System.Text.Json.JsonSerializer.Serialize(
                new{ id=33,trackName="Upgrade Original",artistName="Own Artist",
                    albumName="Album",duration=200,hasWordSync=true,
                    lyricsfile=safeYaml,syncedLyrics="[00:01.20]Hello future"});
            var stub=new StubLyricsHttp((req,ct)=>{
                string path=req.RequestUri!.AbsolutePath;
                string query=req.RequestUri.Query;
                if(path=="/api/get")
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "{\"trackName\":\"Upgrade Original\","+
                        "\"artistName\":\"Own Artist\",\"duration\":200,"+
                        "\"syncedLyrics\":\"[00:01.20]Hello future\"}"));
                if(path=="/api/search" && query.Contains("q=",StringComparison.Ordinal))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("["+word+"]"));
                return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
            });
            using var service=new LyricsService(stub);
            var first=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Upgrade Original","Own Artist","Album",TimeSpan.FromSeconds(200),
                CancellationToken.None)).GetAwaiter().GetResult();
            var upgraded=System.Threading.Tasks.Task.Run(()=>service.TryUpgradeWordsAsync(
                "Upgrade Original","Own Artist","Album",TimeSpan.FromSeconds(200),
                CancellationToken.None)).GetAwaiter().GetResult();
            var panel=new System.Windows.Controls.WrapPanel();
            var karaoke=new KaraokeWords(panel);
            if(first.Quality!="Line" || upgraded?.Quality!="Word" ||
                !karaoke.Setup(upgraded.Lines[0]))return false;
            karaoke.Update(TimeSpan.FromMilliseconds(1300));
            return karaoke.CurrentWordIndex==0&&stub.Calls==3;
        });
        Check("Universal: broad query never accepts a wrong live/remix version",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.AbsolutePath=="/api/search" &&
                   req.RequestUri.Query.Contains("q=",StringComparison.Ordinal))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "[{\"id\":44,\"trackName\":\"Safe Song (Live)\","+
                        "\"artistName\":\"Own Artist\",\"duration\":184,"+
                        "\"syncedLyrics\":\"[00:01.00]Unrelated version\"}]"));
                if(req.RequestUri.AbsolutePath=="/api/search")
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
                return System.Threading.Tasks.Task.FromResult(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Safe Song","Own Artist","",TimeSpan.FromSeconds(184),
                CancellationToken.None)).GetAwaiter().GetResult();
            return !result.Synchronized&&result.Quality=="Keine";
        });
        Check("Universal: plain online lyrics upgrade to true word timestamps",()=>{
            string lyricsfile=System.Text.Json.JsonSerializer.Serialize(originalYaml);
            var stub=new StubLyricsHttp((req,ct)=>{
                string path=req.RequestUri!.AbsolutePath;
                if(path=="/api/get")return System.Threading.Tasks.Task.FromResult(
                    StubLyricsHttp.Json("{\"trackName\":\"Plain Upgrade\","+
                        "\"artistName\":\"Own Artist\",\"duration\":200,"+
                        "\"plainLyrics\":\"Hello future\"}"));
                if(path=="/api/search"&&req.RequestUri.Query.Contains("q=",StringComparison.Ordinal))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "[{\"id\":555,\"trackName\":\"Plain Upgrade\","+
                        "\"artistName\":\"Own Artist\",\"duration\":200,"+
                        "\"hasWordSync\":true,\"lyricsfile\":"+lyricsfile+"}]"));
                return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Plain Upgrade","Own Artist","",TimeSpan.FromSeconds(200),
                CancellationToken.None)).GetAwaiter().GetResult();
            return result.Quality=="Word"&&result.Lines.Count==1 &&
                result.Lines[0].Words is {Count:2}&&stub.Calls==3;
        });
        Check("Universal: modest duration mismatch shows plain, never wrong timestamps",()=>{
            string entries="[{\"id\":801,\"trackName\":\"November\","+
                "\"artistName\":\"JM7\",\"duration\":202,"+
                "\"plainLyrics\":\"A sample original verse\","+
                "\"syncedLyrics\":\"[00:08.00]A sample original verse\"}]";
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.AbsolutePath=="/api/search")
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(entries));
                return System.Threading.Tasks.Task.FromResult(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "November","JM7","",TimeSpan.FromSeconds(190),
                CancellationToken.None)).GetAwaiter().GetResult();
            if(result.Quality!="Plain"||result.Synchronized||result.Lines.Count!=0||
                !result.Source.Contains("ähnliche")||
                result.PlainText!="A sample original verse")return false;
            var host=new MainWindow();
            typeof(MainWindow).GetField("_lyricResult",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)?.SetValue(host,result);
            typeof(MainWindow).GetField("_plainLyrics",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)?.SetValue(host,result.PlainText);
            typeof(MainWindow).GetMethod("ShowPlainLyrics",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic)?.Invoke(host,null);
            return host.TitleRegion.Visibility==System.Windows.Visibility.Visible&&
                host.PlainLyricsReader.Visibility==System.Windows.Visibility.Visible&&
                host.PlainLyricsText.Text.Contains("A sample original verse")&&
                host.PreviousLyric.Text=="";

        });
        Check("Universal: large duration mismatch is rejected even as plain",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.AbsolutePath=="/api/search")
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "[{\"id\":802,\"trackName\":\"November\",\"artistName\":\"JM7\","+
                        "\"duration\":258,\"plainLyrics\":\"Unrelated recording\"}]"));
                return System.Threading.Tasks.Task.FromResult(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "November","JM7","",TimeSpan.FromSeconds(190),
                CancellationToken.None)).GetAwaiter().GetResult();
            return result.Quality=="Keine"&&!result.Synchronized;
        });
        Check("Lyrics multi-provider plain fallback does not fabricate sync",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                string endpoint=req.RequestUri!.AbsolutePath;
                if(req.RequestUri.Host=="api.lyrics.ovh")
                    return System.Threading.Tasks.Task.FromResult(
                        StubLyricsHttp.Json("{\"lyrics\":\"Original sample stanza\"}"));
                if(endpoint.EndsWith("/api/search",StringComparison.Ordinal))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
                return System.Threading.Tasks.Task.FromResult(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Original Sample","Sample Singer","Demo Album",
                TimeSpan.FromSeconds(211),CancellationToken.None)).GetAwaiter().GetResult();
            return stub.Calls==4 && result.Quality=="Plain" &&
                result.Source=="lyrics.ovh" && !result.Synchronized &&
                result.Lines.Count==0 && result.PlainText=="Original sample stanza";
        });
        Check("Lyrics positive cache avoids repeated network calls",()=>{
            var stub=new StubLyricsHttp((req,ct)=>System.Threading.Tasks.Task.FromResult(
                StubLyricsHttp.Json("{\"trackName\":\"Original Song\","+
                "\"artistName\":\"Test Singer\",\"albumName\":\"Test Album\","+
                "\"duration\":200,\"syncedLyrics\":\"[00:01.00]Example\"}")));
            using var service=new LyricsService(stub);
            var first=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Original Song","Test Singer","Test Album",
                TimeSpan.FromSeconds(200),CancellationToken.None)).GetAwaiter().GetResult();
            int before=stub.Calls;
            var second=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Original Song","Test Singer","Test Album",
                TimeSpan.FromSeconds(200),CancellationToken.None)).GetAwaiter().GetResult();
            return first.Synchronized && second.Synchronized &&
                before==1 && stub.Calls==1 && service.LastCacheHit;
        });
        Check("Lyrics unavailable search uses short-term negative cache",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.AbsolutePath.Contains("/search"))
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json("[]"));
                return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(
                    System.Net.HttpStatusCode.NotFound));
            });
            using var service=new LyricsService(stub);
            var first=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Missing Original","Sample Singer","Album",
                TimeSpan.FromSeconds(200),CancellationToken.None)).GetAwaiter().GetResult();
            int calls=stub.Calls;
            var again=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Missing Original","Sample Singer","Album",
                TimeSpan.FromSeconds(200),CancellationToken.None)).GetAwaiter().GetResult();
            return !first.Synchronized && stub.Calls==calls && service.LastCacheHit;
        });
        Check("Lyrics provider 429 falls back without unauthorized requests",()=>{
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.Host=="lrclib.net"){
                    var limited=new System.Net.Http.HttpResponseMessage(
                        System.Net.HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromSeconds(60));
                    return System.Threading.Tasks.Task.FromResult(limited);
                }
                return System.Threading.Tasks.Task.FromResult(
                    StubLyricsHttp.Json("{\"lyrics\":\"Example sample\"}"));
            });
            using var service=new LyricsService(stub);
            var result=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Sample Anthem","Sample Singer","Album",
                TimeSpan.FromSeconds(200),CancellationToken.None)).GetAwaiter().GetResult();
            return result.Quality=="Plain" && stub.Calls==2;
        });
        Check("Lyrics requests cancel on song change without stale data",()=>{
            var stub=new StubLyricsHttp(async(req,ct)=>{
                await System.Threading.Tasks.Task.Delay(800,ct);
                return StubLyricsHttp.Json("{}");
            });
            using var service=new LyricsService(stub);
            using var cts=new CancellationTokenSource(35);
            try{
                System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                    "Aborted Original","Sample Singer","Album",
                    TimeSpan.FromSeconds(200),cts.Token)).GetAwaiter().GetResult();
                return false;
            }catch(OperationCanceledException){return true;}
        });

        Check("Lyrics alternate selection persists chosen LRCLIB id",()=>{
            string dir=Path.Combine(Path.GetTempPath(),"CVChoice-"+Guid.NewGuid().ToString("N"));
            try{
                var local=new LyricsLibrary(dir);
                var stub=new StubLyricsHttp((req,ct)=>{
                    string endpoint=req.RequestUri!.AbsolutePath;
                    if(endpoint=="/api/get")
                        return System.Threading.Tasks.Task.FromResult(
                            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                    if(endpoint=="/api/search")
                        return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                            "[{\"id\":101,\"trackName\":\"Original Choice\","+
                            "\"artistName\":\"Test Singer\",\"albumName\":\"Album\","+
                            "\"duration\":182,\"syncedLyrics\":\"[00:01.00]Sample A\"},"+
                            "{\"id\":102,\"trackName\":\"Original Choice\","+
                            "\"artistName\":\"Test Singer\",\"albumName\":\"Album\","+
                            "\"duration\":183,\"syncedLyrics\":\"[00:01.00]Sample B\"}]"));
                    return System.Threading.Tasks.Task.FromResult(StubLyricsHttp.Json(
                        "{\"id\":102,\"trackName\":\"Original Choice\","+
                        "\"artistName\":\"Test Singer\",\"albumName\":\"Album\","+
                        "\"duration\":183,\"syncedLyrics\":\"[00:01.00]Sample B\"}"));
                });
                using var service=new LyricsService(stub,local);
                var initial=System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                    "Original Choice","Test Singer","Album",
                    TimeSpan.FromSeconds(183),CancellationToken.None)).GetAwaiter().GetResult();
                var selected=System.Threading.Tasks.Task.Run(()=>service.ChooseAlternativeAsync(
                    102,"Original Choice","Test Singer","Album",
                    TimeSpan.FromSeconds(183),CancellationToken.None)).GetAwaiter().GetResult();
                var persisted=new LyricsLibrary(dir);
                return initial.Synchronized&&selected is {Synchronized:true}&&
                    service.Alternatives(LyricsService.SongKey("Original Choice",
                        "Test Singer","Album",TimeSpan.FromSeconds(183))).Count==2&&
                    persisted.SelectedId(LyricsService.SongKey("Original Choice",
                        "Test Singer","Album",TimeSpan.FromSeconds(183)))==102;
            }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        });
        Check("Lyrics 429 cooldown skips LRCLIB on next song",()=>{
            int lrclib=0,plain=0;
            var stub=new StubLyricsHttp((req,ct)=>{
                if(req.RequestUri!.Host=="lrclib.net"){
                    System.Threading.Interlocked.Increment(ref lrclib);
                    var limited=new System.Net.Http.HttpResponseMessage(
                        System.Net.HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromMinutes(1));
                    return System.Threading.Tasks.Task.FromResult(limited);
                }
                System.Threading.Interlocked.Increment(ref plain);
                return System.Threading.Tasks.Task.FromResult(
                    StubLyricsHttp.Json("{\"lyrics\":\"Example sample\"}"));
            });
            using var service=new LyricsService(stub);
            System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Original A","Sample Singer","Album",null,CancellationToken.None))
                .GetAwaiter().GetResult();
            System.Threading.Tasks.Task.Run(()=>service.GetAsync(
                "Original B","Sample Singer","Album",null,CancellationToken.None))
                .GetAwaiter().GetResult();
            return lrclib==1&&plain==2;
        });
        Check("Lyrics settings menu builds safely before Spotify title",()=>{
            var window=new MainWindow();
            var menu=typeof(MainWindow).GetMethod("BuildMenu",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance);
            var built=menu?.Invoke(window,null) as System.Windows.Controls.ContextMenu;
            return built is not null&&built.Items.Count>5;
        });
        Check("Lyrics library invalid key cannot crash first launch",()=>{
            var library=new LyricsLibrary(Path.Combine(Path.GetTempPath(),
                "CV-empty-"+Guid.NewGuid().ToString("N")));
            return !library.HasImport("")&&!library.HasImport("bad/path");
        });

        Check("Chromatic settings: six sections render with clear content",()=>{
            var host=new MainWindow();
            var testWindow=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(testWindow).EnsureHandle();
            using var hotkeys=new HotkeyManager(testWindow,new AppSettings(),_=>{},_=>{});
            var panel=new SettingsWindow(host,new AppSettings(),hotkeys);
            var select=typeof(SettingsWindow).GetMethod("Select",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            bool okay=true;
            foreach(var name in new[]{"Playback","Lyrics","Layout","Design","Keys","App"}){
                select?.Invoke(panel,new object[]{name});
                okay&=panel.SettingsContent.Children.Count>0 &&
                    !string.IsNullOrWhiteSpace(panel.SectionTitle.Text);
            }
            return okay;
        });
        Check("Chromatic palette: neutral is readable, accents adapt",()=>{
            var neutral=AlbumPalette.Neutral;
            var red=AlbumPalette.Analyze(Sample(234,32,48));
            var blue=AlbumPalette.Analyze(Sample(42,84,233));
            return neutral.Monochrome &&
                red.Accent.R>red.Accent.B &&
                blue.Accent.B>blue.Accent.R &&
                red.Highlight!=blue.Highlight;
        });
        Check("Chromatic V3: one transition emits at most 25 shared updates",()=>{
            var engine=new ChromaticDesignEngine();
            int count=0;
            var red=AlbumPalette.Analyze(Sample(244,44,59));
            engine.Changed+=tokens=>{
                count++;
                if(tokens.TextPrimary.A!=255)
                    throw new Exception("Text alpha changed");
            };
            engine.SetTarget(red);
            for(int i=0;i<110;i++)engine.Tick(.009);
            return count>=20 && count<=25 && !engine.Transitioning &&
                engine.Tokens.Palette.Equals(red) &&
                engine.UpdatesEmitted==count;
        });
        Check("Chromatic V3: red-blue change is shared and interruptible",()=>{
            var engine=new ChromaticDesignEngine();
            var red=AlbumPalette.Analyze(Sample(239,49,61));
            var blue=AlbumPalette.Analyze(Sample(42,83,238));
            engine.SetTarget(red);
            engine.Tick(.32);
            var middle=engine.Tokens.Palette;
            engine.SetTarget(blue);
            engine.Tick(.36);
            var blend=engine.Tokens.Palette;
            engine.Tick(.5);
            return !middle.Equals(red)&&!blend.Equals(blue) &&
                engine.Tokens.Palette.Equals(blue);
        });
        Check("Chromatic V3: readable tokens for dark, red and blue albums",()=>{
            var palettes=new[]{
                AlbumPalette.Neutral,
                AlbumPalette.Analyze(Sample(32,25,39)),
                AlbumPalette.Analyze(Sample(237,40,64)),
                AlbumPalette.Analyze(Sample(33,68,247))
            };
            var canvas=System.Windows.Media.Color.FromRgb(18,29,39);
            return palettes.All(p=>{
                var t=ChromaticTokens.From(p);
                return ChromaticTokens.Contrast(t.TextPrimary,canvas)>=7 &&
                    ChromaticTokens.Contrast(t.TextSecondary,canvas)>=4.5 &&
                    ChromaticTokens.Contrast(t.TextActive,canvas)>=4.5 &&
                    ChromaticTokens.Contrast(t.Icon,canvas)>=5;
            });
        });
        Check("Chromatic V3: reduced motion finishes transition immediately",()=>{
            var engine=new ChromaticDesignEngine();
            var blue=AlbumPalette.Analyze(Sample(33,83,244));
            engine.SetTarget(blue);
            engine.Tick(.016,true);
            return !engine.Transitioning &&
                engine.Tokens.Palette.Equals(blue);
        });
        Check("Music-adaptive vinyl: real periodic bass gives plausible BPM",()=>{
            var tempo=new MusicTempoEstimator();
            var levels=new float[92];
            for(int i=0;i<660;i++){
                float bass=i%25==0?.91f:.06f;
                for(int band=0;band<12;band++)levels[band]=bass;
                tempo.Observe(i*.02,levels);
            }
            return tempo.EstimatedBpm is double bpm &&
                Math.Abs(bpm-120)<1 && tempo.Confidence>.9 &&
                MusicTempoEstimator.TargetRpm(bpm)>33.333;
        });
        Check("Music-adaptive vinyl: irregular pulses never invent stable BPM",()=>{
            var tempo=new MusicTempoEstimator();
            var levels=new float[92];
            double time=0;
            foreach(double delay in new[]{.36,.94,.41,.72,1.05,.38,.78,.43,.89,.36,.95,.48}){
                time+=delay;
                for(int band=0;band<12;band++)levels[band]=.04f;
                tempo.Observe(time-.015,levels);
                for(int band=0;band<12;band++)levels[band]=.91f;
                tempo.Observe(time,levels);
            }
            return tempo.EstimatedBpm is null &&
                Math.Abs(MusicTempoEstimator.TargetRpm(null)-33.333)<.001;
        });
        Check("Music-adaptive vinyl: new target glides without angular jumps",()=>{
            var disc=new VinylVisual();
            disc.SetTrack(true,null,false);
            disc.Tick(2,null);
            double before=disc.RotationDegrees;
            disc.TargetRotationRpm=46;
            disc.Tick(.02,null);
            double delta=(disc.RotationDegrees-before+360)%360;
            disc.Tick(3,null);
            return delta>2 && delta<7 &&
                disc.RotationSpeed>202 && disc.RotationSpeed<276;
        });
        Check("Updater: strict version checks and no automatic installation",()=>{
            return VisionUpdateService.IsNewer("1.0.0","1.0.1") &&
                !VisionUpdateService.IsNewer("1.0.1","1.0.0") &&
                !VisionUpdateService.IsNewer("1.0.0","invalid") &&
                !VisionUpdateService.Shared.CanInstall;
        });
        Check("Global hotkeys: safe configurable defaults",()=>{
            return HotkeyManager.Defaults[VisionAction.PlayPause]=="Ctrl+Alt+P" &&
                HotkeyManager.Defaults.ContainsKey(VisionAction.PositionMode) &&
                HotkeyManager.Defaults.ContainsKey(VisionAction.CycleProfile) &&
                !new AppSettings().GlobalHotkeysEnabled;
        });
        Check("Keybind manager: safe defaults, conflicts and reserved keys",()=>{
            var view=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(view).EnsureHandle();
            var settings=new AppSettings();
            using var keys=new HotkeyManager(view,settings,_=>{},_=>{});
            return !keys.Enabled&&keys.RegisteredCount==0 &&
                keys.Binding(VisionAction.PlayPause)=="Ctrl+Alt+P" &&
                keys.Validate(VisionAction.Next,"Ctrl+Alt+Left")?.Contains("vergeben")==true &&
                keys.Validate(VisionAction.Next,"Win+R")?.Contains("reserviert")==true &&
                keys.Validate(VisionAction.Next,"Alt+Tab")?.Contains("reserviert")==true &&
                keys.Validate(VisionAction.Next,"F")?.Contains("Modifikatortaste")==true;
        });
        Check("Keybind manager: modify/unbind/reset without user config writes",()=>{
            var view=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(view).EnsureHandle();
            var settings=new AppSettings();
            using var keys=new HotkeyManager(view,settings,_=>{},_=>{});
            bool success=keys.Configure(VisionAction.Mute,"Ctrl+Alt+U",out _) &&
                keys.Binding(VisionAction.Mute)=="Ctrl+Alt+U" &&
                !keys.Configure(VisionAction.Next,"Ctrl+Alt+U",out _) &&
                keys.Configure(VisionAction.Mute,"",out _) &&
                keys.Binding(VisionAction.Mute)=="";
            keys.Reset();
            return success&&keys.Binding(VisionAction.Mute)=="Ctrl+Alt+M" &&
                !settings.GlobalHotkeysEnabled;
        });
        Check("Keybind safety: only configured combinations can be global",()=>{
            var view=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(view).EnsureHandle();
            var settings=new AppSettings();
            using var keys=new HotkeyManager(view,settings,_=>{},_=>{});
            return keys.Validate(VisionAction.Next,"Ctrl+Alt+Delete")!=null &&
                keys.Validate(VisionAction.Previous,"Ctrl+Shift+Escape")!=null &&
                keys.Validate(VisionAction.VolumeUp,"Ctrl+Alt+F12")==null &&
                keys.RegisteredCount==0;
        });
        Check("Chromatic palette: updating frozen WPF brushes is safe",()=>{
            var host=new MainWindow();
            var testWindow=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(testWindow).EnsureHandle();
            using var manager=new HotkeyManager(testWindow,new AppSettings(),_=>{},_=>{});
            var panel=new SettingsWindow(host,new AppSettings(),manager);
            var apply=typeof(SettingsWindow).GetMethod("ApplyPalette",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Instance);
            apply?.Invoke(panel,null);
            var brush=(SolidColorBrush)panel.Resources["AccentBrush"];
            if(brush.IsFrozen) return false;
            var before=brush.Color;
            apply?.Invoke(panel,null);
            return ((SolidColorBrush)panel.Resources["AccentBrush"]).Color==before;
        });
        Check("Chromatic settings: hotkeys and UI preferences survive JSON roundtrip",()=>{
            var sample=new AppSettings {
                AlbumColorsEnabled=false,GlobalHotkeysEnabled=false,
                VinylScale=1.1,LyricsScale=1.25,LyricsSpacing=30,
                AudioSensitivity=1.5,AudioSmoothing=1.8,
                PositionLocked=true,LyricsGlow=.35,
                Hotkeys=new Dictionary<string,string>{
                    [VisionAction.Next.ToString()]="Ctrl+Alt+N"
                }
            };
            var encoded=System.Text.Json.JsonSerializer.Serialize(sample);
            var decoded=System.Text.Json.JsonSerializer.Deserialize<AppSettings>(encoded);
            return decoded is not null && !decoded.GlobalHotkeysEnabled &&
                !decoded.AlbumColorsEnabled && decoded.PositionLocked &&
                Math.Abs(decoded.VinylScale-1.1)<.0001 &&
                Math.Abs(decoded.LyricsScale-1.25)<.0001 &&
                decoded.Hotkeys[VisionAction.Next.ToString()]=="Ctrl+Alt+N";
        });
        Check("Infinity: album crossfade preserves active word and progress",()=>{
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel){GlowStrength=.78,SizeScale=1.2};
            var words=new []{
                new LyricWord(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),"Hello "),
                new LyricWord(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(3),"world")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),"Hello world",words)))
                return false;
            renderer.ApplyTheme(AlbumPalette.Default);
            renderer.Update(TimeSpan.FromMilliseconds(1600));
            var block=(System.Windows.Controls.TextBlock)panel.Children[0];
            var gradient=block.Foreground as System.Windows.Media.LinearGradientBrush;
            if(gradient is null || renderer.CurrentWordIndex!=0)return false;
            double before=gradient.GradientStops[1].Offset;
            renderer.ApplyTheme(AlbumPalette.Neutral);
            renderer.Update(TimeSpan.FromMilliseconds(1750));
            return renderer.CurrentWordIndex==0 &&
                object.ReferenceEquals(block.Foreground,gradient) &&
                gradient.GradientStops[1].Offset>before &&
                block.Effect is System.Windows.Media.Effects.DropShadowEffect &&
                ((System.Windows.Media.ScaleTransform)block.RenderTransform).ScaleX>1.04 &&
                block.FontSize>49;
        });
        Check("Infinity: lyrics backward seek clears old glow",()=>{
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel);
            var words=new[]{
                new LyricWord(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),"First "),
                new LyricWord(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(3),"second")
            };
            if(!renderer.Setup(new LyricLine(TimeSpan.FromSeconds(1),"First second",words)))
                return false;
            renderer.Update(TimeSpan.FromMilliseconds(2500));
            var second=(System.Windows.Controls.TextBlock)panel.Children[1];
            if(second.Effect is null)return false;
            renderer.Update(TimeSpan.FromMilliseconds(900));
            return second.Effect is null && renderer.CurrentWordIndex==-1 &&
                ((System.Windows.Media.ScaleTransform)second.RenderTransform).ScaleX==1;
        });
        Check("Infinity: compact glass menu matches narrow reference layout",()=>{
            var host=new MainWindow();
            var hwndWindow=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(hwndWindow).EnsureHandle();
            var settings=new AppSettings {GlobalHotkeysEnabled=false};
            using var keys=new HotkeyManager(hwndWindow,settings,_=>{},_=>{});
            var glass=new GlassMenuWindow(host,settings,keys,new System.Windows.Point(640,400));
            var build=typeof(GlassMenuWindow).GetMethod("Rebuild",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            build?.Invoke(glass,null);
            var rows=glass.MenuItems.Children.OfType<System.Windows.Controls.Button>().ToArray();
            var flyout=glass.Flyout;
            return glass.Width>=360&&glass.Width<=450 && glass.Height<=800 &&
                rows.Length>=17 && flyout.Placement==
                    System.Windows.Controls.Primitives.PlacementMode.Custom &&
                glass.MenuItems.Children.Count>=23;
        });
        Check("Infinity: word progress follows monotonic clock without extra wait",()=>{
            var panel=new System.Windows.Controls.WrapPanel();
            var karaoke=new KaraokeWords(panel) {GlowStrength=.8};
            var words=new[]{
                new LyricWord(TimeSpan.FromMilliseconds(1000),
                    TimeSpan.FromMilliseconds(1800),"Sing "),
                new LyricWord(TimeSpan.FromMilliseconds(1800),
                    TimeSpan.FromMilliseconds(2600),"along")
            };
            if(!karaoke.Setup(new LyricLine(TimeSpan.FromSeconds(1),"Sing along",words)))
                return false;
            karaoke.ApplyTheme(AlbumPalette.Default);
            var clock=new PrecisionPlaybackClock();
            long stamp=PrecisionPlaybackClock.Stamp();
            var utc=DateTimeOffset.UtcNow;
            clock.Update(new PlaybackClock(TimeSpan.FromMilliseconds(1100),
                TimeSpan.FromMinutes(3),utc,true,true),1,stamp,utc,true);
            karaoke.Update(clock.Estimated(stamp)!.Value);
            var first=(System.Windows.Controls.TextBlock)panel.Children[0];
            var before=((System.Windows.Media.LinearGradientBrush)first.Foreground)
                .GradientStops[1].Offset;
            karaoke.Update(clock.Estimated(stamp+
                System.Diagnostics.Stopwatch.Frequency/4)!.Value);
            return karaoke.CurrentWordIndex==0 &&
                ((System.Windows.Media.LinearGradientBrush)first.Foreground)
                    .GradientStops[1].Offset>before &&
                first.Effect is System.Windows.Media.Effects.DropShadowEffect;
        });
        Check("Repair: legacy settings enable safe plain-lyrics fallback",()=>{
            var old=new AppSettings {PlainLyricsReader=false,LyricsDisplayVersion=0};
            var method=typeof(SettingsStore).GetMethod("Normalize",
                System.Reflection.BindingFlags.NonPublic|
                System.Reflection.BindingFlags.Static);
            var migrated=(AppSettings?)method?.Invoke(null,new object[]{old});
            return migrated is not null && migrated.PlainLyricsReader &&
                migrated.LyricsDisplayVersion==1 &&
                migrated.VinylCenterTransparent;
        });
        Check("Repair: center is true alpha, with optional dark center",()=>{
            byte RenderCenterAlpha(bool transparentMode){
                // A fresh WPF visual is needed for RenderTargetBitmap to avoid
                // retaining the previous cached drawing outside a window tree.
                var disc=new VinylVisual {Width=350,Height=350,
                    CenterTransparent=transparentMode};
                disc.Measure(new System.Windows.Size(350,350));
                disc.Arrange(new System.Windows.Rect(0,0,350,350));
                var bmp=new System.Windows.Media.Imaging.RenderTargetBitmap(
                    350,350,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(disc);
                var bytes=new byte[350*350*4];
                bmp.CopyPixels(bytes,350*4,0);
                return bytes[(175*350+175)*4+3];
            }
            byte transparent=RenderCenterAlpha(true);
            byte dark=RenderCenterAlpha(false);
            if(transparent!=0 || dark==0)
                throw new InvalidOperationException($"Center alpha: transparent={transparent}, dark={dark}");
            return true;
        });
        Check("Repair: glass pane clips the wallpaper to rounded corners",()=>{
            var host=new MainWindow();
            var handle=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(handle).EnsureHandle();
            using var manager=new HotkeyManager(handle,new AppSettings(),_=>{},_=>{});
            var glass=new GlassMenuWindow(host,new AppSettings(),manager,
                new System.Windows.Point(300,300));
            glass.UpdateCornerClip(new System.Windows.Size(386,768));
            return glass.GlassContentGrid.Clip is
                System.Windows.Media.RectangleGeometry mask &&
                mask.RadiusX>=26 && mask.RadiusY>=26;
        });
        Check("Repair: packaged Windows icon has seven PNG alpha sizes",()=>{
            var stream=System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/CharliVision.ico"));
            if(stream is null)return false;
            using(stream.Stream)
            using(var b=new System.IO.BinaryReader(stream.Stream)){
                return b.ReadUInt16()==0 && b.ReadUInt16()==1 &&
                    b.ReadUInt16()==7;
            }
        });
        Check("Repair: plain lyrics display visibly without timestamps",()=>{
            var host=new MainWindow();
            var settingsField=typeof(MainWindow).GetField("_settings",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            var settings=(AppSettings?)settingsField?.GetValue(host);
            if(settings is null)return false;
            settings.PlainLyricsReader=true;
            var plain=typeof(MainWindow).GetField("_plainLyrics",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            plain?.SetValue(host,"A sample line\nAnother sample line");
            var display=typeof(MainWindow).GetMethod("ShowPlainLyrics",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            display?.Invoke(host,null);
            return host.TitleRegion.Visibility==System.Windows.Visibility.Visible &&
                host.PlainLyricsReader.Visibility==System.Windows.Visibility.Visible &&
                host.PlainLyricsText.Text.Contains("A sample line") &&
                host.PreviousLyric.Text=="" && host.NextLyric.Text=="";

        });
        Check("Repair: closing the glass menu twice is safe",()=>{
            var host=new MainWindow();
            var handle=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(handle).EnsureHandle();
            using var manager=new HotkeyManager(handle,new AppSettings(),_=>{},_=>{});
            var glass=new GlassMenuWindow(host,new AppSettings(),manager,
                new System.Windows.Point(350,400));
            glass.Show();
            var close=typeof(GlassMenuWindow).GetMethod("RequestClose",
                System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.NonPublic);
            close?.Invoke(glass,null);
            close?.Invoke(glass,null);
            return !glass.IsVisible;
        });
        Check("AutoSync: worker JSON produces real word-based renderer cues",()=>{
            const string payload=@"{
              ""engine"":""whisperx-forced-alignment"",
              ""language"":""en"",""duration_ms"":4000,""coverage"":1,
              ""matched_words"":2,""source_word_count"":2,
              ""uncertain_lines"":[],""lines"":[{
                ""text"":""Hello world"",""start_ms"":1000,""words"":[
                {""text"":""Hello "",""start_ms"":1000,""end_ms"":1600,""score"":0.9},
                {""text"":""world"",""start_ms"":1600,""end_ms"":2400,""score"":0.9}]
              }]}";
            var preview=LocalAlignmentService.Parse(payload,"test.wav","own.txt");
            var yaml=preview.ToLyricsfile();
            var reparsed=WordLyricsParser.ParseLyricsfile(yaml);
            if(reparsed.Count!=1||reparsed[0].Words is not {Count:2})
                return false;
            var panel=new System.Windows.Controls.WrapPanel();
            var renderer=new KaraokeWords(panel){GlowStrength=.65};
            if(!renderer.Setup(reparsed[0]))return false;
            renderer.Update(TimeSpan.FromMilliseconds(1250));
            return renderer.CurrentWordIndex==0;
        });
        Check("AutoSync: confirmed word timings reuse local song cache",()=>{
            string root=Path.Combine(Path.GetTempPath(),"CVAutoSyncTest_"+
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                var lib=new LyricsLibrary(root);
                string key=LyricsService.SongKey("Owned Demo","Test Artist",
                    "Local",TimeSpan.FromSeconds(4));
                var sample=new LocalAlignmentService.Preview("own.wav","own.txt","en",
                    4000,1,2,2,Array.Empty<int>(),new[]{
                        new LocalAlignmentService.Line("Hello world",1000,
                            new[]{
                                new LocalAlignmentService.Word("Hello ",1000,1600,.91),
                                new LocalAlignmentService.Word("world",1600,2300,.89)})
                    });
                lib.ImportWords(key,sample.ToLyricsfile());
                using var service=new LyricsService(null,lib);
                var task=service.GetAsync("Owned Demo","Test Artist","Local",
                    TimeSpan.FromSeconds(4),CancellationToken.None);
                var result=task.GetAwaiter().GetResult();
                return result.Quality=="Word"&&result.Lines.Count==1 &&
                    result.Lines[0].Words is {Count:2};
            } finally {
                try{Directory.Delete(root,true);}catch{}
            }
        });
        Check("AutoSync: rejects nonexistent or invented word timings",()=>{
            const string invalid=@"{""language"":""en"",""duration_ms"":4000,
              ""coverage"":1,""matched_words"":1,""source_word_count"":1,
              ""uncertain_lines"":[],""lines"":[{
                ""text"":""Hello"",""start_ms"":1000,""words"":[
                 {""text"":""Hello"",""start_ms"":3500,
                  ""end_ms"":4500,""score"":0.9}]}]}";
            try{
                LocalAlignmentService.Parse(invalid,"test.wav","own.txt");
                return false;
            }catch(InvalidDataException){return true;}
        });
        Check("AutoSync: actual settings page offers local opt-in workflow",()=>{
            var host=new MainWindow();
            var helper=new System.Windows.Window();
            new System.Windows.Interop.WindowInteropHelper(helper).EnsureHandle();
            using var hotkeys=new HotkeyManager(helper,new AppSettings(),_=>{},_=>{});
            var window=new SettingsWindow(host,new AppSettings(),hotkeys);
            window.NavigateTo("Lyrics");
            var cards=window.SettingsContent.Children.OfType<System.Windows.Controls.Border>();
            var text=cards.SelectMany(b=>
                (b.Child as System.Windows.Controls.StackPanel)?.Children
                    .OfType<System.Windows.Controls.TextBlock>()
                    ??Enumerable.Empty<System.Windows.Controls.TextBlock>())
                .Select(t=>t.Text).ToArray();
            return text.Any(s=>s.Contains("LOKALE KI-WORTSYNCHRONISATION")) &&
                text.Any(s=>s.Contains("Eigene Audiodateien") ||
                    s.Contains("ausgewählte eigene Audiodateien"));
        });
        // A limited live service smoke test using LRCLIB's published API documentation example.
        // Never print or persist returned lyrics.
        try {
            using var lyrics=new LyricsService();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(14));
            var result=System.Threading.Tasks.Task.Run(() => lyrics.GetAsync(
                "I Want to Live","Borislav Slavov",
                "Baldur's Gate 3 (Original Game Soundtrack)",TimeSpan.FromSeconds(233),
                timeout.Token)).GetAwaiter().GetResult();
            report.Add("LIVE LRCLIB: "+(result.Synchronized?"PASS (synced cues="+result.Lines.Count+")":
                "NOT VERIFIED ("+result.Status+")"));
        } catch(Exception ex){
            report.Add("LIVE LRCLIB: NOT VERIFIED ("+ex.GetType().Name+")");
        }
        // Publicly available word-synced LRCLIB example. Only counts are logged.
        try {
            using var client=new System.Net.Http.HttpClient {Timeout=TimeSpan.FromSeconds(12)};
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CharliVision/1.1");
            var response=client.GetStringAsync("https://lrclib.net/api/get/8168177")
                .GetAwaiter().GetResult();
            using var json=System.Text.Json.JsonDocument.Parse(response);
            string yaml=json.RootElement.GetProperty("lyricsfile").GetString()??"";
            var parsed=WordLyricsParser.ParseLyricsfile(yaml);
            int realWordCount=parsed.Sum(line=>line.Words?.Count??0);
            report.Add(realWordCount>0 ?
                $"LIVE WORD-SYNC: PASS (lines={parsed.Count}, timed segments={realWordCount})" :
                "LIVE WORD-SYNC: NOT VERIFIED (source contains no validated word segments)");
        } catch(Exception ex) {
            report.Add("LIVE WORD-SYNC: NOT VERIFIED ("+ex.GetType().Name+")");
        }
        report.Add("Pure tests failed="+failed);
        try {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,"self-test.txt"),report);
        } catch(Exception ex) {SettingsStore.Log(ex);}
        return failed==0;
    }
    private static BitmapSource Sample(byte r,byte g,byte b)
    {
        var data=new byte[32*32*4];
        for(int i=0;i<data.Length;i+=4){
            data[i]=b;data[i+1]=g;data[i+2]=r;data[i+3]=255;
        }
        var bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,data,32*4);
        bitmap.Freeze();
        return bitmap;
    }
}