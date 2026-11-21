using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace CharliVision;

/// <summary>
/// Small, local-only editable representation. Never guesses word end times.
/// Uses existing LRC and Lyricsfile v1 parsers for final validation.
/// </summary>
public static class LyricsEditorDocument
{
    public const string Word="Word",Line="Line",Plain="Plain";
    public static string Export(LyricResult result)
    {
        if(result.Quality==Plain)
            return result.PlainText??string.Empty;
        if(result.Lines.Count==0)return string.Empty;
        if(result.Lines.Any(l=>l.Words is {Count:>0})){
            var lines=new List<Dictionary<string,object>>();
            foreach(var line in result.Lines){
                var obj=new Dictionary<string,object>{
                    ["text"]=line.Text,
                    ["start_ms"]=(long)Math.Round(line.Time.TotalMilliseconds)
                };
                if(line.Words is {Count:>0} words){
                    var list=new List<Dictionary<string,object>>();
                    foreach(var word in words){
                        var w=new Dictionary<string,object>{
                            ["text"]=word.Text,
                            ["start_ms"]=(long)Math.Round(word.Start.TotalMilliseconds)
                        };
                        if(word.End is TimeSpan end)
                            w["end_ms"]=(long)Math.Round(end.TotalMilliseconds);
                        list.Add(w);
                    }
                    obj["words"]=list;
                }
                lines.Add(obj);
            }
            var serializer=new SerializerBuilder().Build();
            return serializer.Serialize(new Dictionary<string,object>{
                ["version"]="1.0",
                ["metadata"]=new Dictionary<string,string>{
                    ["editor"]="CHARLI VISION local"},
                ["lines"]=lines
            });
        }
        return string.Join(Environment.NewLine,result.Lines.Select(l=>
            $"[{Stamp(l.Time)}]{l.Text}"))+Environment.NewLine;
    }
    private static string Stamp(TimeSpan time)
    {
        long ms=(long)Math.Round(Math.Max(0,time.TotalMilliseconds));
        return string.Format(CultureInfo.InvariantCulture,
            "{0:00}:{1:00}.{2:00}",ms/60_000,(ms/1000)%60,(ms%1000)/10);
    }
    public static LyricResult Validate(string raw,string mode)
    {
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>250_000)
            throw new ArgumentException("Text fehlt oder ist größer als 250 KB");
        if(mode==Plain)
            return new LyricResult(Array.Empty<LyricLine>(),
                "Eigener Lesetext",false,"Eigener Editor",Plain,raw);
        if(mode==Line){
            var lines=LyricsService.ParseLrc(raw);
            if(lines.Count==0 || lines.Count>500)
                throw new ArgumentException("Keine gültigen Zeitmarken gefunden");
            return new LyricResult(lines,"Eigene Zeitkorrektur",true,
                "Eigener Editor",lines.Any(l=>l.Words is {Count:>0})?Word:Line);
        }
        if(mode==Word){
            var lines=WordLyricsParser.ParseLyricsfile(raw);
            if(lines.Count==0 || !lines.Any(l=>l.Words is {Count:>0}))
                throw new ArgumentException("Ungültige Lyricsfile-Wortzeitstempel");
            return new LyricResult(lines,"Eigene Wortzeitkorrektur",true,
                "Eigener Editor",Word);
        }
        throw new ArgumentException("Unbekanntes Editor-Format");
    }
    public static string ShiftCurrentTimestamp(string content,int selection,int deltaMs)
    {
        if(content.Length==0)return content;
        int index=Math.Clamp(selection,0,content.Length);
        int start=content.LastIndexOf('\n',Math.Max(0,index-1))+1;
        int end=content.IndexOf('\n',index);
        if(end<0)end=content.Length;
        string line=content[start..end];
        // A selected YAML word start/end is edited by exact milliseconds.
        var yaml=Regex.Match(line,@"\b(?:start_ms|end_ms):\s*(\d{1,9})");
        if(yaml.Success){
            var num=yaml.Groups[1];
            if(!long.TryParse(num.Value,out long current))return content;
            long shifted=Math.Clamp(current+deltaMs,0,18_000_000);
            int from=start+num.Index;
            return content[..from]+shifted.ToString(CultureInfo.InvariantCulture)+
                content[(from+num.Length)..];
        }
        // Line-based (Enhanced) LRC: change the selected line start tag.
        var lrc=Regex.Match(line,@"\[(\d{1,3}):(\d{2})[.,](\d{2,3})\]");
        if(!lrc.Success)return content;
        var match=lrc;
        long minutes=long.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);
        long secs=long.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
        string fraction=match.Groups[3].Value;
        long ms=minutes*60_000+secs*1000+
            long.Parse(fraction,CultureInfo.InvariantCulture)*(fraction.Length==2?10:1);
        long corrected=Math.Clamp(ms+deltaMs,0,12_000_000);
        var replacement="["+Stamp(TimeSpan.FromMilliseconds(corrected))+"]";
        return content[..(start+match.Index)]+replacement+
            content[(start+match.Index+match.Length)..];
    }
}
