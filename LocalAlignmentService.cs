using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace CharliVision;

/// <summary>
/// One-shot OFFLINE user-owned audio alignment; never hooks Spotify/WASAPI streams.
/// Model and Python runtime are independent of the WPF application and optional.
/// </summary>
public static class LocalAlignmentService
{
    public sealed record Word(string Text,long StartMs,long EndMs,double Score);
    public sealed record Line(string Text,long StartMs,IReadOnlyList<Word> Words);
    public sealed record Preview(string AudioPath,string LyricsPath,string Language,
        long DurationMs,double Coverage,int MatchedWords,int SourceWords,
        IReadOnlyList<int> UncertainLines,IReadOnlyList<Line> Lines)
    {
        public double TextSimilarity {get;init;}=double.NaN;
        public bool FromCache {get;init;}
        public bool HasWordTiming=>Lines.Any(l=>l.Words.Count>0);
        public IReadOnlyList<LyricLine> ToLyricLines()=>Lines.Select(l=>
            new LyricLine(TimeSpan.FromMilliseconds(l.StartMs),l.Text,
                l.Words.Count==0?null:l.Words.Select(w=>
                    new LyricWord(TimeSpan.FromMilliseconds(w.StartMs),
                        TimeSpan.FromMilliseconds(w.EndMs),w.Text)).ToArray())).ToArray();
        public string ToLyricsfile()
        {
            var list=new List<Dictionary<string,object>>();
            foreach(var l in Lines){
                var row=new Dictionary<string,object>{
                    ["text"]=l.Text,["start_ms"]=l.StartMs
                };
                if(l.Words.Count>0) row["words"]=l.Words.Select(w=>
                    new Dictionary<string,object>{
                        ["text"]=w.Text,["start_ms"]=w.StartMs,["end_ms"]=w.EndMs
                    }).ToArray();
                list.Add(row);
            }
            var yaml=new SerializerBuilder().Build().Serialize(
                new Dictionary<string,object>{
                    ["version"]="1.0",
                    ["metadata"]=new Dictionary<string,string>{
                        ["source"]="Local user-owned audio",["language"]=Language},
                    ["lines"]=list
                });
            var reparsed=WordLyricsParser.ParseLyricsfile(yaml);
            if(reparsed.Count!=Lines.Count)
                throw new InvalidDataException("Generated lyrics failed round-trip validation.");
            if(HasWordTiming&&!reparsed.Any(x=>x.Words is {Count:>0}))
                throw new InvalidDataException("No word timestamps survived import validation.");
            return yaml;
        }
        public string ToEnhancedLrc()
        {
            static string Stamp(long ms,char bracketLeft,char bracketRight){
                ms=Math.Max(0,ms);
                long min=ms/60_000;
                long sec=(ms%60_000)/1000;
                long hund=(ms%1000)/10;
                return $"{bracketLeft}{min:00}:{sec:00}.{hund:00}{bracketRight}";
            }
            var sb=new StringBuilder();
            sb.AppendLine("[by:CHARLI VISION - local user-provided audio]");
            foreach(var line in Lines){
                sb.Append(Stamp(line.StartMs,'[',']'));
                if(line.Words.Count>0){
                    foreach(var word in line.Words)
                        sb.Append(Stamp(word.StartMs,'<','>')).Append(word.Text);
                }else sb.Append(line.Text);
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
    public sealed record EnvironmentConfig(string Python,string Worker,string Cache);

    private static readonly string ConfigPath=Path.Combine(
        SettingsStore.DirectoryPath,"alignment-engine.json");
    public static bool TryGetConfig(out EnvironmentConfig? result)
    {
        result=null;
        try {
            if(!File.Exists(ConfigPath))return false;
            using var doc=JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root=doc.RootElement;
            string python=root.GetProperty("python").GetString()??"";
            string worker=root.GetProperty("worker").GetString()??"";
            string cache=root.GetProperty("cache").GetString()??"";
            if(!Path.IsPathFullyQualified(python)||!Path.IsPathFullyQualified(worker)||
                !Path.IsPathFullyQualified(cache)||!File.Exists(python)||!File.Exists(worker))
                return false;
            result=new EnvironmentConfig(python,worker,cache);
            return true;
        }catch(Exception ex){SettingsStore.Log(ex);return false;}
    }
    public static Preview Parse(string json,string audioPath,string lyricsPath)
    {
        if(json.Length>1_000_000)throw new InvalidDataException("Alignment output too large");
        using var document=JsonDocument.Parse(json);
        var root=document.RootElement;
        var duration=root.GetProperty("duration_ms").GetInt64();
        var coverage=root.GetProperty("coverage").GetDouble();
        double textSimilarity=root.TryGetProperty("asr_text_consistency",out var check)?
            check.GetDouble():double.NaN;
        if(double.IsFinite(textSimilarity) && (textSimilarity<.5||textSimilarity>1))
            throw new InvalidDataException("Audio and source lyrics do not match reliably");
        if(duration<=0||duration>480_000||coverage<0||coverage>1)
            throw new InvalidDataException("Invalid alignment duration/coverage");
        var lines=new List<Line>();
        foreach(var part in root.GetProperty("lines").EnumerateArray()){
            if(lines.Count>=500)throw new InvalidDataException("Too many aligned lines");
            string text=part.GetProperty("text").GetString()??"";
            long at=part.GetProperty("start_ms").GetInt64();
            if(text.Length>400||at<0||at>duration)throw new InvalidDataException("Invalid line");
            var words=new List<Word>();
            foreach(var item in part.GetProperty("words").EnumerateArray()){
                if(words.Count>=80)throw new InvalidDataException("Too many words");
                string chunk=item.GetProperty("text").GetString()??"";
                long start=item.GetProperty("start_ms").GetInt64();
                long end=item.GetProperty("end_ms").GetInt64();
                double score=item.GetProperty("score").GetDouble();
                if(chunk.Length>120||start<at||end<start||end>duration+100||
                    score<0||score>1 ||
                    (words.Count>0&&start<words[^1].StartMs))
                    throw new InvalidDataException("Unverified or out-of-order word");
                words.Add(new Word(chunk,start,end,score));
            }
            if(words.Count>0 && !string.Equals(
                Regex.Replace(string.Concat(words.Select(w=>w.Text)),@"\s+"," ").Trim(),
                Regex.Replace(text,@"\s+"," ").Trim(),StringComparison.Ordinal))
                throw new InvalidDataException("Alignment does not match source words");
            lines.Add(new Line(text,at,words));
        }
        if(lines.Count==0||lines.Zip(lines.Skip(1),(a,b)=>a.StartMs<=b.StartMs)
               .Any(valid=>!valid))
            throw new InvalidDataException("No ordered aligned lines");
        var uncertain=root.GetProperty("uncertain_lines").EnumerateArray()
            .Select(x=>x.GetInt32()).Take(500).ToArray();
        return new Preview(audioPath,lyricsPath,
            root.GetProperty("language").GetString()??"en",duration,coverage,
            root.GetProperty("matched_words").GetInt32(),
            root.GetProperty("source_word_count").GetInt32(),uncertain,lines){
                TextSimilarity=textSimilarity
            };
    }
    private static string CacheFileFor(string audio,string lyrics,string language)
    {
        var directory=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "CharliVision","AlignmentResults");
        if(new FileInfo(audio).Length>250L*1024*1024 ||
            new FileInfo(lyrics).Length>200_000)
            throw new InvalidDataException("Alignment input too large");
        using var sound=File.OpenRead(audio);
        using var textFile=File.OpenRead(lyrics);
        var signature="cv-alignment-asr-v2|"+Convert.ToHexString(SHA256.HashData(sound))+
            "|"+Convert.ToHexString(SHA256.HashData(textFile))+"|"+language;
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        return Path.Combine(directory,key+".json");
    }
    public static async Task<Preview> AlignAsync(
        string audioPath,string lyricsPath,string language,
        IProgress<string>? progress,CancellationToken cancellation)
    {
        if(!TryGetConfig(out var env) || env is null)
            throw new InvalidOperationException(
                "Lokale KI-Umgebung nicht eingerichtet. Spotify und Lyrics funktionieren ohne sie.");
        if(!File.Exists(audioPath)||!File.Exists(lyricsPath))
            throw new FileNotFoundException("Audio or own lyrics file does not exist");
        cancellation.ThrowIfCancellationRequested();
        string cacheFile=CacheFileFor(audioPath,lyricsPath,language);
        if(File.Exists(cacheFile)){
            try{
                var cached=Parse(await File.ReadAllTextAsync(cacheFile,cancellation),
                    audioPath,lyricsPath);
                if(double.IsFinite(cached.TextSimilarity) && cached.TextSimilarity>=.5){
                    progress?.Report("100 Wiederverwendete lokale Wortzeitstempel");
                    return cached with {FromCache=true};
                }
            }catch(OperationCanceledException){throw;}
            catch{try{File.Delete(cacheFile);}catch{}}
        }
        string output=Path.Combine(Path.GetTempPath(),
            "charlivision-align-"+Guid.NewGuid().ToString("N")+".json");
        using var process=new Process();
        var start=new ProcessStartInfo(env.Python){
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        foreach(var item in new[]{"-u",env.Worker,"--audio",audioPath,
                "--lyrics",lyricsPath,"--language",language,"--output",output,
                "--cache",env.Cache})
            start.ArgumentList.Add(item);
        start.Environment["CUDA_VISIBLE_DEVICES"]="";
        start.Environment["HF_HOME"]=Path.Combine(env.Cache,"huggingface");
        start.Environment["OMP_NUM_THREADS"]="4";
        process.StartInfo=start;
        var stderr=new StringBuilder();
        try{
            if(!process.Start())throw new InvalidOperationException("AI worker did not start");
            using var cancel=cancellation.Register(()=>{
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
            });
            Task errorTask=Task.Run(async ()=>{
                while(await process.StandardError.ReadLineAsync() is string line){
                    if(stderr.Length<2400)stderr.AppendLine(line);
                }
            });
            while(await process.StandardOutput.ReadLineAsync(cancellation) is string line){
                if(line.StartsWith("PROGRESS\t",StringComparison.Ordinal))
                    progress?.Report(line.Replace('\t',' '));
            }
            await process.WaitForExitAsync(cancellation);
            await errorTask;
            if(process.ExitCode!=0 || !File.Exists(output))
                throw new InvalidOperationException("Lokale Audioausrichtung fehlgeschlagen: "+
                    (stderr.Length>0?stderr.ToString()[..Math.Min(1100,stderr.Length)]:
                        "Unbekannter Worker-Fehler"));
            var json=await File.ReadAllTextAsync(output,cancellation);
            var parsed=Parse(json,audioPath,lyricsPath);
            if(double.IsFinite(parsed.TextSimilarity)&&parsed.TextSimilarity>=.5){
                try{
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                    var cacheTemp=cacheFile+".tmp."+Guid.NewGuid().ToString("N");
                    File.WriteAllText(cacheTemp,json,new UTF8Encoding(false));
                    File.Move(cacheTemp,cacheFile,true);
                    foreach(var old in Directory.GetFiles(Path.GetDirectoryName(cacheFile)!,
                            "*.json").Select(x=>new FileInfo(x))
                        .OrderByDescending(x=>x.LastWriteTimeUtc).Skip(40)){
                        try{old.Delete();}catch{}
                    }
                }catch(Exception ex){SettingsStore.Log(ex);}
            }
            return parsed;
        }finally{
            try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
            try{File.Delete(output);}catch{}
        }
    }
}
