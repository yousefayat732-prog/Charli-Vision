using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace CharliVision;

/// <summary>
/// Strict TTML reader for cached Better Lyrics responses.
/// Uses ONLY timestamps actually present in the document; no inferred word timing.
/// DTD and external entities are disabled and the input size is bounded.
/// </summary>
internal static class BetterLyricsTtml
{
    private static bool Stamp(string? value,out TimeSpan time)
    {
        time=default;
        if(string.IsNullOrWhiteSpace(value))return false;
        var parts=value.Split(':');
        if(parts.Length<1||parts.Length>3)return false;
        if(!double.TryParse(parts[^1],NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,out double seconds) ||
            !double.IsFinite(seconds) || seconds<0 ||
            (parts.Length>1&&seconds>=60))return false;
        long minutes=0,hours=0;
        if(parts.Length>=2 && (!long.TryParse(parts[^2],NumberStyles.None,
                CultureInfo.InvariantCulture,out minutes) || minutes<0 ||
                (parts.Length==3&&minutes>=60)))return false;
        if(parts.Length==3 && (!long.TryParse(parts[0],NumberStyles.None,
                CultureInfo.InvariantCulture,out hours)||hours<0))return false;
        double total=hours*3600d+minutes*60d+seconds;
        if(total>10800)return false;
        time=TimeSpan.FromMilliseconds(Math.Round(total*1000));
        return true;
    }
    private static string? Attr(XElement item,string name)=>
        item.Attributes().FirstOrDefault(x=>x.Name.LocalName==name)?.Value;

    public static IReadOnlyList<LyricLine> Parse(string? raw,TimeSpan? duration=null)
    {
        if(string.IsNullOrWhiteSpace(raw)||raw.Length>200_000)
            return Array.Empty<LyricLine>();
        try{
            using var reader=XmlReader.Create(new StringReader(raw),
                new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,
                    XmlResolver=null,MaxCharactersInDocument=220_000,
                    IgnoreComments=true});
            // Ignore inter-element indentation; preserve the meaningful text
            // stored inside lyric spans without adding formatting whitespace.
            var document=XDocument.Load(reader,LoadOptions.None);
            if(document.Root?.Name.LocalName!="tt")
                return Array.Empty<LyricLine>();
            var lines=new List<LyricLine>();
            foreach(var p in document.Descendants().Where(e=>e.Name.LocalName=="p")){
                if(lines.Count>=1500)return Array.Empty<LyricLine>();
                if(!Stamp(Attr(p,"begin"),out var start))continue;
                if(duration is {TotalSeconds:>1} && start>duration.Value+TimeSpan.FromSeconds(4))
                    continue;
                var spans=p.Descendants().Where(e=>e.Name.LocalName=="span" &&
                    !e.Elements().Any(child=>child.Name.LocalName=="span")).ToArray();
                // Providers pretty-print spans with indentation/newlines between
                // them. Only span text represents timed lyrics; whitespace
                // between XML elements is not part of the song text.
                string text=(spans.Length>0?
                    string.Concat(spans.Select(x=>x.Value)):p.Value).Trim();
                if(text.Length==0||text.Length>400)continue;
                var words=new List<LyricWord>();
                bool hasEnd=Stamp(Attr(p,"end"),out var lineEnd);
                bool valid=spans.Length is >0 and <=70 && hasEnd && lineEnd>start;
                if(valid){
                    foreach(var s in spans){
                        string part=s.Value;
                        if(string.IsNullOrWhiteSpace(part)||
                           !Stamp(Attr(s,"begin"),out var begin) ||
                           !Stamp(Attr(s,"end"),out var end) ||
                           begin<start||end<=begin||end>lineEnd ||
                           (words.Count>0&&begin<words[^1].Start)){
                            valid=false;break;
                        }
                        words.Add(new LyricWord(begin,end,part));
                    }
                    if(valid&&!string.Equals(string.Concat(words.Select(w=>w.Text)).Trim(),
                        text,StringComparison.Ordinal))valid=false;
                }
                lines.Add(new LyricLine(start,text,valid?words:null));
            }
            return lines.OrderBy(l=>l.Time).DistinctBy(l=>l.Time).ToArray();
        }catch(XmlException){return Array.Empty<LyricLine>();}
        catch(InvalidOperationException){return Array.Empty<LyricLine>();}
    }
}