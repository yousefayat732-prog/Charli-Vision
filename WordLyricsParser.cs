using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace CharliVision;

// LRCLIB Lyricsfile v1: timestamps are real milliseconds from the source, not estimates.
public static class WordLyricsParser
{
    public static IReadOnlyList<LyricLine> ParseLyricsfile(string yaml)
    {
        if(string.IsNullOrWhiteSpace(yaml) || yaml.Length>250_000)
            return Array.Empty<LyricLine>();
        try {
            var document=new YamlStream();
            document.Load(new StringReader(yaml));
            if(document.Documents.Count!=1 || document.Documents[0].RootNode is not YamlMappingNode root
                || Scalar(root,"version")!="1.0"
                || Field(root,"lines") is not YamlSequenceNode lineNodes
                || lineNodes.Children.Count>500) return Array.Empty<LyricLine>();
            var output=new List<LyricLine>();
            int wordCount=0;
            foreach(var node in lineNodes.Children) {
                if(node is not YamlMappingNode line)continue;
                var time=Milliseconds(line,"start_ms");
                var text=Scalar(line,"text");
                if(time is null || text is null || text.Length>400)continue;
                var words=new List<LyricWord>();
                if(Field(line,"words") is YamlSequenceNode wordNodes && wordNodes.Children.Count<=80) {
                    foreach(var part in wordNodes.Children) {
                        if(part is not YamlMappingNode item)continue;
                        var start=Milliseconds(item,"start_ms");
                        var end=Milliseconds(item,"end_ms");
                        var segment=Scalar(item,"text");
                        if(start is null || segment is null || segment.Length>120)continue;
                        if(end is not null && end<start)continue;
                        if(start<time)continue;
                        words.Add(new LyricWord(start.Value,end,segment));
                        if(++wordCount>7000)return Array.Empty<LyricLine>();
                    }
                }
                if(words.Count>0) {
                    bool valid=words.Zip(words.Skip(1),(a,b)=>a.Start<=b.Start).All(x=>x);
                    // Only highlight a word when the source provides the same exact text.
                    var fromWords=Regex.Replace(string.Concat(words.Select(w=>w.Text)),@"\s+"," ").Trim();
                    var fromLine=Regex.Replace(text,@"\s+"," ").Trim();
                    if(!valid || !string.Equals(fromWords,fromLine,StringComparison.Ordinal))
                        words.Clear();
                }
                output.Add(new LyricLine(time.Value,text,
                    words.Count>0?words.AsReadOnly():null));
            }
            return output.OrderBy(x=>x.Time).ToArray();
        }catch {
            // Invalid/untrusted remote YAML is never allowed to break the overlay.
            return Array.Empty<LyricLine>();
        }
    }
    private static YamlNode? Field(YamlMappingNode map,string key)
    {
        foreach(var pair in map.Children) {
            if(pair.Key is YamlScalarNode scalar && scalar.Value==key)return pair.Value;
        }
        return null;
    }
    private static string? Scalar(YamlMappingNode map,string field) =>
        (Field(map,field) as YamlScalarNode)?.Value;
    private static TimeSpan? Milliseconds(YamlMappingNode map,string key)
    {
        string? raw=Scalar(map,key);
        if(!long.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out long ms)
            || ms<0 || ms>18_000_000) return null;
        return TimeSpan.FromMilliseconds(ms);
    }
}