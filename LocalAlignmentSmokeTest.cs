using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CharliVision;

/// <summary>Opt-in real local WAV -> WhisperX -> timestamp -> WPF renderer smoke test.</summary>
public static class LocalAlignmentSmokeTest
{
    public static bool Run()
    {
        var log=new List<string>{
            "CHARLI VISION — LOCAL ALIGNMENT REAL END-TO-END TEST",
            DateTimeOffset.Now.ToString("u"),
            "User-owned Windows-generated speech WAV; never Spotify-protected audio."
        };
        bool success=false;
        try{
            if(!LocalAlignmentService.TryGetConfig(out var config)||config is null)
                throw new InvalidOperationException("Local AI environment not configured");
            var folder=Path.GetDirectoryName(config.Worker)!;
            var audio=Path.Combine(folder,"tests","demo-voice.wav");
            var text=Path.Combine(folder,"tests","demo-voice.txt");
            if(!File.Exists(audio)||!File.Exists(text))
                throw new FileNotFoundException("Owned test WAV/TXT not available");
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));
            // Do not block WPF's synchronization context during Python execution.
            var result=Task.Run(()=>LocalAlignmentService.AlignAsync(
                audio,text,"en",null,timeout.Token)).GetAwaiter().GetResult();
            var yaml=result.ToLyricsfile();
            var parsed=WordLyricsParser.ParseLyricsfile(yaml);
            log.Add("WORKER=WhisperX CPU");
            log.Add($"WORDS={result.MatchedWords}/{result.SourceWords}");
            log.Add($"LINES={result.Lines.Count}");
            log.Add($"COVERAGE={result.Coverage}");
            log.Add($"UNCERTAIN_LINES={result.UncertainLines.Count}");
            log.Add("YAML_ROUNDTRIP_LINES="+parsed.Count);
            var preview=new AlignmentPreviewWindow(result,AlbumPalette.Default);
            try{
                preview.Show();
                var first=parsed.FirstOrDefault(x=>x.Words is {Count:>0});
                if(first is null)throw new InvalidDataException("No actual word timing");
                var sample=first.Words![0].Start+TimeSpan.FromMilliseconds(170);
                preview.RenderAt(sample);
                bool highlighted=preview.CurrentWordIndex==0;
                log.Add("WPF_KARAOKE_HIGHLIGHT="+highlighted);
                preview.UpdateLayout();
                var width=Math.Max(640,(int)Math.Ceiling(preview.ActualWidth));
                var height=Math.Max(320,(int)Math.Ceiling(preview.ActualHeight));
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                bitmap.Render(preview);
                var png=new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                string imagePath=Path.Combine(SettingsStore.DirectoryPath,
                    "alignment-karaoke-preview.png");
                using(var stream=File.Create(imagePath))png.Save(stream);
                log.Add("SCREENSHOT="+imagePath);
                success=highlighted&&parsed.Count==result.Lines.Count&&
                    result.MatchedWords>=5&&result.HasWordTiming;
            }finally{preview.Close();}
        }catch(Exception ex){
            log.Add("FAILED="+ex.GetType().Name+": "+ex.Message);
            SettingsStore.Log(ex);
        }
        log.Add("RESULT="+(success?"PASS":"FAIL"));
        try {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                "alignment-selftest.txt"),log);
        }catch{}
        return success;
    }
}
