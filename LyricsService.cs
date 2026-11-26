using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CharliVision;

public sealed record LyricWord(TimeSpan Start,TimeSpan? End,string Text);
public sealed record LyricLine(TimeSpan Time,string Text,IReadOnlyList<LyricWord>? Words=null);
public sealed record LyricResult(IReadOnlyList<LyricLine> Lines,string Status,bool Synchronized,
    string Source="Keine",string Quality="Keine",string? PlainText=null)
{
    public static LyricResult Unavailable(string why)=>new(Array.Empty<LyricLine>(),why,false);
}
public sealed record LyricAlternative(int Id,string Title,string Artist,string Album,
    double Duration,string Quality,int Score);

// The original WPF renderer, playback clock, lyrics offsets and media service are unchanged.
public sealed class LyricsService : IDisposable
{
    private sealed record CacheEntry(LyricResult Value,DateTimeOffset Expires);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests=new(1,1);
    private readonly object _lock=new();
    private readonly Dictionary<string,CacheEntry> _cache=new();
    private readonly Queue<string> _order=new();
    private readonly Dictionary<string,IReadOnlyList<LyricAlternative>> _choices=new();
    private readonly HashSet<string> _alreadySearched=new();
    private readonly HashSet<string> _wordUpgradeSearched=new();
    private DateTimeOffset _lrclibRetryAfter,_ovhRetryAfter,_betterLyricsRetryAfter;
    private readonly LyricsLibrary _library;
    private readonly PersistentLyricsCache? _diskCache;
    public LyricsLibrary Library=>_library;
    public double LastLookupMs{get;private set;}
    public double LastUpgradeMs{get;private set;}
    public bool LastCacheHit{get;private set;}
    public string LastSource{get;private set;}="Keine";
    public string LastQuality{get;private set;}="Keine";
    public string LastSearchStatus{get;private set;}="Noch keine Suche";
    public DateTimeOffset LastSearchAt{get;private set;}=DateTimeOffset.MinValue;
    public int LastProviderAttempts{get;private set;}
    private static readonly Regex Stamp=new(
        @"\[(\d{1,3}):(\d{2})(?:[\.,](\d{1,3}))?\]",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex EnhancedStamp=new(
        @"<(\d{1,3}):(\d{2})(?:[\.,](\d{1,3}))?>",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex StripPunctuation=new(
        @"[^\p{L}\p{N}]",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex Version=new(
        @"\b(live|remix|acoustic|instrumental|radio[ -]?edit|extended|sped[ -]?up|slowed|nightcore|demo|karaoke|remaster(?:ed)?|re[ -]?recorded|clean|explicit|mono|stereo|cover[ -]?version|edit[ -]?version)\b",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex Features=new(
        @"\s*[\[(]?(?:feat\.?|ft\.?|featuring)\s+[^\]\)]*[\])]?",
        RegexOptions.IgnoreCase|RegexOptions.Compiled|RegexOptions.CultureInvariant);

    public LyricsService(HttpMessageHandler? transport=null,LyricsLibrary? library=null)
        :this(transport,library,null){}
    internal LyricsService(HttpMessageHandler? transport,LyricsLibrary? library,
        PersistentLyricsCache? diskCache)
    {
        _library=library??new LyricsLibrary();
        _diskCache=diskCache??(transport is null && library is null?
            new PersistentLyricsCache(Path.Combine(SettingsStore.DirectoryPath,"LyricsCacheV10")):null);
        _http=transport is null?new HttpClient():new HttpClient(transport);
        _http.Timeout=TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CharliVision/1.5");
        // Lrclib-App-Id may only be sent for a properly registered app.
        // An invented value triggers HTTP 401 and silently makes all synced
        // lyrics disappear. Anonymous requests work with Lrclib-Client.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client",
            "CharliVision/1.5 (free native Windows desktop overlay)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    public static string SongKey(string title,string artist,string album,TimeSpan? duration)=>
        LyricsSyncSettings.SongKey(title,artist,album,duration);
    public IReadOnlyList<LyricAlternative> Alternatives(string songKey)
    {
        lock(_lock) return _choices.TryGetValue(songKey,out var found)?
            found:Array.Empty<LyricAlternative>();
    }
    public void Invalidate(string songKey)
    {
        lock(_lock){
            _cache.Remove(songKey);
            _alreadySearched.Remove(songKey);
            _wordUpgradeSearched.Remove(songKey);
        }
        // Keep the last positive result for offline fallback on a forced rescan.
    }
    private void Remember(string key,LyricResult result)
    {
        var ttl=result.Synchronized||result.Quality=="Plain"?
            TimeSpan.FromHours(6):result.Status.Contains("nicht erreichbar",
            StringComparison.OrdinalIgnoreCase) ||
            result.Status.Contains("Rate",StringComparison.OrdinalIgnoreCase)?
            TimeSpan.FromSeconds(45):TimeSpan.FromMinutes(7);
        lock(_lock){
            if(!_cache.ContainsKey(key))_order.Enqueue(key);
            _cache[key]=new CacheEntry(result,DateTimeOffset.UtcNow+ttl);
            while(_cache.Count>40 && _order.Count>0)_cache.Remove(_order.Dequeue());
        }
        _diskCache?.Write(key,result);
    }
    private void RememberChoices(string key,IReadOnlyList<LyricAlternative> candidates)
    {
        lock(_lock){
            if(_choices.Count>=40&&!_choices.ContainsKey(key))
                _choices.Remove(_choices.Keys.First());
            _choices[key]=candidates;
        }
    }
    private void MarkSearched(string key)
    {
        lock(_lock){
            if(_alreadySearched.Count>=45)_alreadySearched.Clear();
            _alreadySearched.Add(key);
        }
    }
    private void SetStatus(LyricResult result)
    {
        LastSource=result.Source;
        LastQuality=result.Quality;
        LastSearchStatus=result.Status.Length==0?"Gefunden":result.Status;
    }
    public async Task<LyricResult> GetAsync(string title,string artist,string album,
        TimeSpan? duration,CancellationToken token,bool force=false)
    {
        if(string.IsNullOrWhiteSpace(title)||string.IsNullOrWhiteSpace(artist)||
            title.Equals("CHARLI VISION",StringComparison.OrdinalIgnoreCase))
            return LyricResult.Unavailable("Spotify-Titel fehlt");
        string key=SongKey(title,artist,album,duration);
        string? ownAligned=_library.GetImportWords(key);
        if(!string.IsNullOrWhiteSpace(ownAligned)){
            var wordLines=WordLyricsParser.ParseLyricsfile(ownAligned);
            if(wordLines.Count>0 && wordLines.Any(l=>l.Words is {Count:>0})){
                var aligned=new LyricResult(wordLines,
                    "Lokal ermittelte und bestätigte Wortzeitstempel",
                    true,"Lokale Audioausrichtung","Word");
                SetStatus(aligned);
                LastCacheHit=true;LastLookupMs=0;return aligned;
            }
        }
        string? local=_library.GetImport(key);
        if(!string.IsNullOrWhiteSpace(local)){
            var parsed=ParseLrc(local);
            var imported=parsed.Count>0 ?
                new LyricResult(parsed,"Lokale LRC-Datei",true,"Eigene Datei",
                    parsed.Any(x=>x.Words is {Count:>0})?"Word":"Line") :
                new LyricResult(Array.Empty<LyricLine>(),"Lokaler Lesetext",false,
                    "Eigene Datei","Plain",local);
            SetStatus(imported);
            LastCacheHit=true;LastLookupMs=0;return imported;
        }
        if(!force){
            lock(_lock){
                if(_cache.TryGetValue(key,out var cached)&&cached.Expires>DateTimeOffset.UtcNow){
                    LastCacheHit=true;LastLookupMs=0;
                    SetStatus(cached.Value);
                    return cached.Value;
                }
            }
        }
        // Read the previous positive match independently of the network.
        // Explicit retry can seek a better version without losing offline text.
        var persisted=_diskCache?.Read(key);
        if(!force && persisted is not null){
            LastCacheHit=true;LastLookupMs=0;LastProviderAttempts=0;
            SetStatus(persisted);return persisted;
        }
        LastCacheHit=false;
        var watch=Stopwatch.StartNew();
        await _requests.WaitAsync(token);
        try {
            token.ThrowIfCancellationRequested();
            if(!force){
                lock(_lock){
                    if(_cache.TryGetValue(key,out var other)&&other.Expires>DateTimeOffset.UtcNow){
                        LastCacheHit=true;LastLookupMs=0;
                        SetStatus(other.Value);
                        return other.Value;
                    }
                }
            }
            LastProviderAttempts=0;LastSearchAt=DateTimeOffset.UtcNow;
            var result=await SearchAsync(title,artist,album,duration,key,token);
            token.ThrowIfCancellationRequested();
            LastLookupMs=watch.Elapsed.TotalMilliseconds;
            if(persisted is not null && QualityScore(persisted.Quality)>QualityScore(result.Quality)){
                LastCacheHit=true;SetStatus(persisted);return persisted;
            }
            Remember(key,result);SetStatus(result);
            return result;
        }finally{_requests.Release();}
    }
    private async Task<LyricResult> SearchAsync(string title,string artist,string album,
        TimeSpan? duration,string key,CancellationToken token)
    {
        LyricResult? best=null;

        bool lrclibError=false;
        if(DateTimeOffset.UtcNow>=_lrclibRetryAfter) {
            int chosen=_library.SelectedId(key);
            if(chosen>0){
                try{
                    LastProviderAttempts++;
                    using var fixedResult=await FetchJson("https://lrclib.net/api/get/"+chosen,
                        "LRCLIB",token);
                    if(fixedResult is null){
                        _library.RemoveSelection(key);
                    }else if(MetadataMatches(fixedResult.RootElement,
                        title,artist,album,duration)){
                        best=Decode(fixedResult.RootElement,"LRCLIB (gewählt)");
                        if(best.Synchronized)return best;
                    }else _library.RemoveSelection(key);
                }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(ProviderCooldownException){lrclibError=true;}
                catch(HttpRequestException){lrclibError=true;}
                catch(Exception){lrclibError=true;}
            }
            if(DateTimeOffset.UtcNow>=_lrclibRetryAfter)try {
                string exact="https://lrclib.net/api/get?track_name="+Uri.EscapeDataString(title)+
                    "&artist_name="+Uri.EscapeDataString(artist);
                if(!string.IsNullOrWhiteSpace(album))
                    exact+="&album_name="+Uri.EscapeDataString(album);
                if(duration is {TotalSeconds:>1 and <3600})
                    exact+="&duration="+Math.Round(duration.Value.TotalSeconds).ToString(
                        CultureInfo.InvariantCulture);
                LastProviderAttempts++;
                using var document=await FetchJson(exact,"LRCLIB",token);
                if(document is not null && MetadataMatches(document.RootElement,
                    title,artist,album,duration)){
                    best=Decode(document.RootElement,"LRCLIB");
                    if(best.Status=="Instrumentaltitel")return best;
                    if(best.Quality=="Word")return best;
                    if(best.Synchronized)return best; // do not delay usable line lyrics
                }
                string url="https://lrclib.net/api/search?track_name="+
                    Uri.EscapeDataString(title)+"&artist_name="+
                    Uri.EscapeDataString(PrimaryArtist(artist));
                LastProviderAttempts++;
                using var search=await FetchJson(url,"LRCLIB",token);
                MarkSearched(key);
                if(search?.RootElement.ValueKind==JsonValueKind.Array){

                    var found=RankAlternatives(search.RootElement,title,artist,album,duration);
                    RememberChoices(key,found);
                    var ordered=found.OrderByDescending(x=>QualityScore(x.Quality))
                        .ThenByDescending(x=>x.Score).ToArray();
                    foreach(var top in ordered){
                        if(QualityScore(top.Quality)<=QualityScore(best?.Quality??"Keine"))
                            break;
                        foreach(var candidate in search.RootElement.EnumerateArray()){
                            if(GetInt(candidate,"id")!=top.Id)continue;
                            var decoded=Decode(candidate,"LRCLIB");
                            if(QualityScore(decoded.Quality)>QualityScore(best?.Quality??"Keine"))
                                best=decoded;
                            break;
                        }
                        if(best?.Quality=="Word")break;
                    }
                }
                if(best?.Synchronized==true)return best;
            }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex){
                lrclibError=true;
                if(ex is not ProviderCooldownException)
                    _lrclibRetryAfter=DateTimeOffset.UtcNow.AddSeconds(25);
            }
        }else lrclibError=true;
        // If exact/metadata LRCLIB yielded only plain text or nothing,
        // try its independent free-text index before falling back to plain.
        // Do not delay an already usable line-synced result.
        if(best?.Synchronized!=true && DateTimeOffset.UtcNow>=_lrclibRetryAfter){
            try {
                var wide=await SearchBroadLrclibAsync(title,artist,album,duration,
                    key,token,best?.Quality??"Keine");
                if(wide is not null)best=wide;
            }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(ProviderCooldownException){lrclibError=true;}
            catch(HttpRequestException){lrclibError=true;}
            catch(Exception ex){
                lrclibError=true;
                SettingsStore.Log(ex);
            }
        }
        if(best?.Synchronized==true)return best;
        // Better Lyrics is a documented cache-first provider used in the
        // YouTube Music ecosystem. No API key, scraping or credentials.
        // A miss never blocks the existing plain-text fallbacks.
        if(DateTimeOffset.UtcNow>=_betterLyricsRetryAfter){
            try{
                var cachedWords=await SearchBetterLyricsCacheAsync(
                    title,artist,album,duration,token);
                if(cachedWords?.Synchronized==true)return cachedWords;
            }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex){
                if(ex is not HttpRequestException && ex is not ProviderCooldownException)
                    SettingsStore.Log(ex);
                _betterLyricsRetryAfter=DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        if(best?.Quality=="Plain")return best;

        // lyrics.ovh is a public plain-lyrics API; it provides no verified timing.
        // Use only when no synchronized LRCLIB result was found.
        if(DateTimeOffset.UtcNow>=_ovhRetryAfter && VersionKey(title).Length==0){
            try {
                LastProviderAttempts++;
                string url="https://api.lyrics.ovh/v1/"+
                    Uri.EscapeDataString(PrimaryArtist(artist))+"/"+
                    Uri.EscapeDataString(title);
                using var doc=await FetchJson(url,"lyrics.ovh",token);
                string? plain=doc is null?null:StringField(doc.RootElement,"lyrics");
                if(!string.IsNullOrWhiteSpace(plain) && plain.Length<=70_000){
                    return new LyricResult(Array.Empty<LyricLine>(),
                        "Lesetext ohne verifizierte Version und Zeitmarken",false,
                        "lyrics.ovh","Plain",plain.Trim());
                }
            }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex){
                if(ex is not ProviderCooldownException)
                    _ovhRetryAfter=DateTimeOffset.UtcNow.AddSeconds(25);
            }
        }
        if(best is not null)return best;
        // lyrics.ovh also exposes a track-suggestion index. Use it only to
        // resolve a verified alternate spelling/artist credit after the direct
        // lookup fails; suggestions alone are never treated as lyrics.
        if(DateTimeOffset.UtcNow>=_ovhRetryAfter &&
            VersionKey(title).Length==0){
            try{
                var suggested=await SearchSuggestedPlainAsync(title,artist,duration,token);
                if(suggested is not null)return suggested;
            }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex){
                if(ex is not ProviderCooldownException)
                    _ovhRetryAfter=DateTimeOffset.UtcNow.AddSeconds(25);
            }
        }
        return LyricResult.Unavailable(lrclibError?
            "Lyrics-Anbieter derzeit nicht erreichbar / Rate-Limit":
            "Kein ausreichend passender Liedtext gefunden");
    }
    private async Task<LyricResult?> SearchBetterLyricsCacheAsync(
        string title,string artist,string album,TimeSpan? duration,CancellationToken token)
    {
        // Cache-only anonymous access; API-key misses return HTTP 401.
        // Do not use fuzzy lyric matches without the verified song duration.
        if(duration is not {TotalSeconds:>10 and <3600} ||
            VersionKey(title).Length>0)return null;
        string url="https://api.betterlyrics.org/getLyrics?s="+
            Uri.EscapeDataString(title)+"&a="+
            Uri.EscapeDataString(PrimaryArtist(artist))+"&al="+
            Uri.EscapeDataString(album??"")+"&d="+
            Math.Round(duration.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        LastProviderAttempts++;
        using var doc=await FetchJson(url,"Better Lyrics",token);
        if(doc is null || doc.RootElement.ValueKind!=JsonValueKind.Object)return null;
        if(DoubleField(doc.RootElement,"score") is double confidence &&
            confidence<.88)return null;
        string? ttml=StringField(doc.RootElement,"ttml");
        var lines=BetterLyricsTtml.Parse(ttml,duration);
        if(lines.Count==0)return null;
        bool words=lines.Any(x=>x.Words is {Count:>0});
        return new LyricResult(lines,
            words?"Wortgenaue Lyrics aus verifiziertem Cache":
                "Zeilensynchronisierte Lyrics aus verifiziertem Cache",
            true,"Better Lyrics (YouTube-Music-Cache)",words?"Word":"Line");
    }
    private async Task<LyricResult?> SearchSuggestedPlainAsync(
        string title,string artist,TimeSpan? duration,CancellationToken token)
    {
        LastProviderAttempts++;
        using var doc=await FetchJson("https://api.lyrics.ovh/suggest/"+
            Uri.EscapeDataString(title+" "+PrimaryArtist(artist)),"lyrics.ovh",token);
        if(doc?.RootElement.ValueKind!=JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data",out var data) ||
            data.ValueKind!=JsonValueKind.Array)return null;
        int attempts=0;
        foreach(var item in data.EnumerateArray()){
            if(item.ValueKind!=JsonValueKind.Object)continue;
            string foundTitle=StringField(item,"title")??"";
            if(TitleKey(foundTitle)!=TitleKey(title) ||
                VersionKey(foundTitle)!=VersionKey(title))continue;
            if(!item.TryGetProperty("artist",out var foundArtist))continue;
            string foundName=StringField(foundArtist,"name")??"";
            if(Normalize(PrimaryArtist(foundName))!=
                Normalize(PrimaryArtist(artist)))continue;
            if(duration is {TotalSeconds:>1} &&
                (DoubleField(item,"duration") is not double seconds ||
                Math.Abs(seconds-duration.Value.TotalSeconds)>4.5))continue;
            // Never repeat the direct request or accept unrelated variants.
            if(string.Equals(foundTitle,title,StringComparison.OrdinalIgnoreCase) &&
                string.Equals(foundName,PrimaryArtist(artist),
                    StringComparison.OrdinalIgnoreCase))continue;
            if(++attempts>2)break;
            LastProviderAttempts++;
            using var lyrics=await FetchJson("https://api.lyrics.ovh/v1/"+
                Uri.EscapeDataString(foundName)+"/"+
                Uri.EscapeDataString(foundTitle),"lyrics.ovh",token);
            string? text=lyrics is null?null:StringField(lyrics.RootElement,"lyrics");
            if(!string.IsNullOrWhiteSpace(text)&&text.Length<=70_000)
                return new LyricResult(Array.Empty<LyricLine>(),
                    "Verifizierter alternativer Lesetext ohne Zeitmarken",false,
                    "lyrics.ovh (alternative Schreibweise)","Plain",text.Trim());
        }
        return null;
    }
    private async Task<LyricResult?> SearchBroadLrclibAsync(string title,string artist,
        string album,TimeSpan? duration,string key,CancellationToken token,
        string minimumQuality)
    {
        // Search both metadata and free text: uploads sometimes omit punctuation,
        // featured artists or album metadata. Exact version/duration verification
        // still applies to every free-text result before accepting it.
        if(DateTimeOffset.UtcNow<_lrclibRetryAfter)return null;
        string query=title+" "+PrimaryArtist(artist);
        string url="https://lrclib.net/api/search?q="+Uri.EscapeDataString(query);
        LastProviderAttempts++;
        using var doc=await FetchJson(url,"LRCLIB",token);
        if(doc?.RootElement.ValueKind!=JsonValueKind.Array)return null;
        var ranked=RankAlternatives(doc.RootElement,title,artist,album,duration);
        var prior=Alternatives(key);
        RememberChoices(key,prior.Concat(ranked).DistinctBy(x=>x.Id)
            .OrderByDescending(x=>QualityScore(x.Quality))
            .ThenByDescending(x=>x.Score).Take(12).ToArray());
        foreach(var alt in ranked){
            if(QualityScore(alt.Quality)<=QualityScore(minimumQuality))
                break;
            foreach(var item in doc.RootElement.EnumerateArray()){
                if(GetInt(item,"id")!=alt.Id)continue;
                var decoded=Decode(item,"LRCLIB (erweiterte Suche)");
                if(QualityScore(decoded.Quality)>QualityScore(minimumQuality))
                    return decoded;
                break;
            }
        }
        return null;
    }
    private sealed class ProviderCooldownException:Exception {}
    private async Task<JsonDocument?> FetchJson(string url,string provider,CancellationToken token)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,url);
        using var response=await _http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        if(response.StatusCode==HttpStatusCode.TooManyRequests){
            var delta=response.Headers.RetryAfter?.Delta??TimeSpan.FromSeconds(60);
            var until=response.Headers.RetryAfter?.Date ??
                DateTimeOffset.UtcNow+delta;
            if(provider=="LRCLIB")_lrclibRetryAfter=until;
            else if(provider=="Better Lyrics")_betterLyricsRetryAfter=until;
            else _ovhRetryAfter=until;
            throw new ProviderCooldownException();
        }
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>750_000)
            throw new InvalidDataException("Oversized lyrics response");
        if(response.Content.Headers.ContentType?.MediaType is not string type ||
            !type.Contains("json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lyrics provider did not return JSON");
        await using var stream=await response.Content.ReadAsStreamAsync(token);
        using var ms=new MemoryStream();
        byte[] buffer=new byte[16*1024];
        int read;
        while((read=await stream.ReadAsync(buffer,token))>0){
            if(ms.Length+read>750_000)throw new InvalidDataException("Lyrics too large");
            ms.Write(buffer,0,read);
        }
        ms.Position=0;
        return await JsonDocument.ParseAsync(ms,new JsonDocumentOptions{MaxDepth=32},token);
    }
    private static string? StringField(JsonElement value,string name)=>
        value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var v) &&
        v.ValueKind==JsonValueKind.String ? v.GetString():null;
    private static double? DoubleField(JsonElement value,string name)=>
        value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var v) &&
        v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var d)?d:null;
    private static int GetInt(JsonElement value,string name)=>
        value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var v)&&
        v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var id)?id:0;
    private static string Normalize(string? text)=>
        StripPunctuation.Replace((text??"").Normalize(NormalizationForm.FormKC),"")
            .ToLowerInvariant();
    private static string PrimaryArtist(string artist)
    {
        string[] parts=Regex.Split(artist,@"\s+(?:feat\.?|ft\.?|with|x)\s+|,|\s+&\s+",
            RegexOptions.IgnoreCase);
        return parts[0].Trim();
    }
    private static string VersionKey(string value)
    {
        var terms=Version.Matches(value.ToLowerInvariant())
            .Select(m=>m.Value.Replace(" ","").Replace("-",""))
            .Distinct().Order().ToArray();
        return string.Join("/",terms);
    }
    private static string TitleKey(string title)=>
        Normalize(Features.Replace(title,""));
    private static LyricResult? SimilarVersionAsPlain(JsonElement entries,
        string title,string artist,TimeSpan? duration)
    {
        // Same title/artist/version, but a DIFFERENT recording length.
        // Never borrow its time tags. Only show original text, clearly
        // labeled as unverified, if the timing difference is modest.
        if(entries.ValueKind!=JsonValueKind.Array||
            duration is not {TotalSeconds:>5 and <3600})return null;
        double maxDeviation=Math.Min(18,
            Math.Max(8,duration.Value.TotalSeconds*.08));
        JsonElement? closest=null;
        double closestDifference=double.MaxValue;
        foreach(var entry in entries.EnumerateArray()){
            if(entry.ValueKind!=JsonValueKind.Object||
                TitleKey(title)!=TitleKey(StringField(entry,"trackName")??"")||
                VersionKey(title)!=VersionKey(StringField(entry,"trackName")??"")||
                Normalize(PrimaryArtist(artist))!=
                    Normalize(PrimaryArtist(StringField(entry,"artistName")??"")))
                continue;
            if(DoubleField(entry,"duration") is not double actual)continue;
            double difference=Math.Abs(actual-duration.Value.TotalSeconds);
            if(difference<=4.5||difference>maxDeviation||
                difference>=closestDifference)continue;
            string? raw=StringField(entry,"plainLyrics");
            if(string.IsNullOrWhiteSpace(raw)||raw.Length>70_000)continue;
            closest=entry;closestDifference=difference;
        }
        if(closest is not JsonElement selected)return null;
        return new LyricResult(Array.Empty<LyricLine>(),
            "Lesetext einer ähnlichen Aufnahme – Laufzeit abweichend, nicht synchronisiert",
            false,"LRCLIB (ähnliche Version)","Plain",
            StringField(selected,"plainLyrics")!.Trim());
    }
    public static bool MetadataMatches(JsonElement entry,string title,string artist,
        string album,TimeSpan? duration)
    {
        if(entry.ValueKind!=JsonValueKind.Object)return false;
        string foundTitle=StringField(entry,"trackName")??StringField(entry,"name")??"";
        string foundArtist=StringField(entry,"artistName")??"";
        if(TitleKey(title)!=TitleKey(foundTitle) ||
            VersionKey(title)!=VersionKey(foundTitle))return false;
        var a=Normalize(PrimaryArtist(artist));
        var b=Normalize(PrimaryArtist(foundArtist));
        if(a.Length<2 || a!=b)return false;
        if(duration is {TotalSeconds:>1} && DoubleField(entry,"duration") is double d
            && Math.Abs(d-duration.Value.TotalSeconds)>4.5)return false;
        // Without a trusted song length, a known different album may refer to
        // a different recording despite the same title/performer. Fail closed.
        string foundAlbum=StringField(entry,"albumName")??"";
        if(duration is not {TotalSeconds:>1} &&
            !string.IsNullOrWhiteSpace(album) &&
            !string.IsNullOrWhiteSpace(foundAlbum) &&
            Normalize(album)!=Normalize(foundAlbum))return false;
        return true;
    }
    private static int Score(JsonElement value,string album,TimeSpan? duration)
    {
        int score=30;
        string foundAlbum=StringField(value,"albumName")??"";
        if(!string.IsNullOrWhiteSpace(album) &&
            Normalize(foundAlbum)==Normalize(album))score+=38;
        if(duration is {TotalSeconds:>1} && DoubleField(value,"duration") is double d)
            score+=Math.Max(0,32-(int)(Math.Abs(d-duration.Value.TotalSeconds)*7));
        else score+=4;
        return score;
    }
    private static int QualityScore(string quality)=>quality switch {
        "Word"=>300,"Line"=>200,"Plain"=>100,_=>0
    };
    public static IReadOnlyList<LyricAlternative> RankAlternatives(JsonElement array,
        string title,string artist,string album,TimeSpan? duration)
    {
        if(array.ValueKind!=JsonValueKind.Array)return Array.Empty<LyricAlternative>();
        var results=new List<LyricAlternative>();
        foreach(var entry in array.EnumerateArray()){
            if(results.Count>=40)break;
            if(!MetadataMatches(entry,title,artist,album,duration))continue;
            int id=GetInt(entry,"id");
            if(id<=0)continue;
            var quality=GetQuality(entry);
            if(quality=="Keine")continue;
            results.Add(new LyricAlternative(id,StringField(entry,"trackName")??title,
                StringField(entry,"artistName")??artist,
                StringField(entry,"albumName")??"",DoubleField(entry,"duration")??0,
                quality,Score(entry,album,duration)));
        }
        return results.OrderByDescending(x=>QualityScore(x.Quality))
            .ThenByDescending(x=>x.Score).DistinctBy(x=>x.Id).Take(12).ToArray();
    }
    private static string GetQuality(JsonElement entry)
    {
        if(entry.TryGetProperty("instrumental",out var instrumental) &&
            instrumental.ValueKind==JsonValueKind.True)return "Keine";
        if(entry.TryGetProperty("hasWordSync",out var word) &&
            word.ValueKind==JsonValueKind.True &&
            !string.IsNullOrWhiteSpace(StringField(entry,"lyricsfile")))return "Word";
        if(!string.IsNullOrWhiteSpace(StringField(entry,"syncedLyrics")))return "Line";
        if(!string.IsNullOrWhiteSpace(StringField(entry,"plainLyrics")))return "Plain";
        return "Keine";
    }
    private static LyricResult Decode(JsonElement obj,string source)
    {
        if(obj.TryGetProperty("instrumental",out var instrumental) &&
            instrumental.ValueKind==JsonValueKind.True)
            return LyricResult.Unavailable("Instrumentaltitel");
        if(obj.TryGetProperty("hasWordSync",out var word) &&
            word.ValueKind==JsonValueKind.True){
            string? file=StringField(obj,"lyricsfile");
            if(!string.IsNullOrWhiteSpace(file)){
                var parsed=WordLyricsParser.ParseLyricsfile(file);
                if(parsed.Any(line=>line.Words is {Count:>0}))
                    return new LyricResult(parsed,"Wortgenaue Lyrics",true,source,"Word");
            }
        }
        string? lrc=StringField(obj,"syncedLyrics");
        if(!string.IsNullOrWhiteSpace(lrc)){
            var parsed=ParseLrc(lrc);
            if(parsed.Count>0)return new LyricResult(parsed,
                "Zeilensynchronisierte Lyrics",true,source,"Line");
        }
        string? plain=StringField(obj,"plainLyrics");
        if(!string.IsNullOrWhiteSpace(plain)&&plain.Length<=70_000)
            return new LyricResult(Array.Empty<LyricLine>(),"Nur Lesetext",false,
                source,"Plain",plain.Trim());
        return LyricResult.Unavailable("Keine passende Synchronisation");
    }
    public async Task<IReadOnlyList<LyricAlternative>> FindAlternativesAsync(
        string title,string artist,string album,TimeSpan? duration,CancellationToken token)
    {
        string key=SongKey(title,artist,album,duration);
        lock(_lock){
            if(_choices.TryGetValue(key,out var cached))return cached;
        }
        if(DateTimeOffset.UtcNow<_lrclibRetryAfter)
            return Array.Empty<LyricAlternative>();
        await _requests.WaitAsync(token);
        try{
            string url="https://lrclib.net/api/search?track_name="+
                Uri.EscapeDataString(title)+"&artist_name="+
                Uri.EscapeDataString(PrimaryArtist(artist));
            using var json=await FetchJson(url,"LRCLIB",token);
            if(json?.RootElement.ValueKind!=JsonValueKind.Array)
                return Array.Empty<LyricAlternative>();
            var candidates=RankAlternatives(json.RootElement,title,artist,album,duration);
            token.ThrowIfCancellationRequested();
            RememberChoices(key,candidates);
            MarkSearched(key);
            return candidates;
        }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch{return Array.Empty<LyricAlternative>();}
        finally{_requests.Release();}
    }
    public async Task<LyricResult?> ChooseAlternativeAsync(int id,string title,
        string artist,string album,TimeSpan? duration,CancellationToken token)
    {
        var key=SongKey(title,artist,album,duration);
        if(!Alternatives(key).Any(x=>x.Id==id))return null;
        await _requests.WaitAsync(token);
        try {
            using var doc=await FetchJson("https://lrclib.net/api/get/"+id,"LRCLIB",token);
            if(doc is null || !MetadataMatches(doc.RootElement,title,artist,album,duration))
                return null;
            var result=Decode(doc.RootElement,"LRCLIB (manuell)");
            if(result.Quality=="Keine")return null;
            token.ThrowIfCancellationRequested();
            _library.Select(key,id);
            Remember(key,result);
            SetStatus(result);
            return result;
        }finally{_requests.Release();}
    }
    public async Task<LyricResult?> TryUpgradeWordsAsync(string title,string artist,
        string album,TimeSpan? duration,CancellationToken token)
    {
        string key=SongKey(title,artist,album,duration);
        if(_library.SelectedId(key)>0)return null;
        bool metadataAlreadySearched;
        lock(_lock){
            // Metadata search may already have run during the initial lookup.
            // A broad search is still worth trying once after usable line cues.
            if(_wordUpgradeSearched.Contains(key))return null;
            if(_wordUpgradeSearched.Count>45)_wordUpgradeSearched.Clear();
            _wordUpgradeSearched.Add(key);
            metadataAlreadySearched=_alreadySearched.Contains(key);
            if(_alreadySearched.Count>45)_alreadySearched.Clear();
            _alreadySearched.Add(key);
        }
        var timer=Stopwatch.StartNew();
        try {
            if(DateTimeOffset.UtcNow<_lrclibRetryAfter)return null;
            await _requests.WaitAsync(token);
            try {
                if(!metadataAlreadySearched){
                    string url="https://lrclib.net/api/search?track_name="+
                        Uri.EscapeDataString(title)+"&artist_name="+
                        Uri.EscapeDataString(PrimaryArtist(artist));
                    LastProviderAttempts++;
                    using var doc=await FetchJson(url,"LRCLIB",token);
                    if(doc?.RootElement.ValueKind==JsonValueKind.Array){
                        var ranked=RankAlternatives(doc.RootElement,
                            title,artist,album,duration);
                        RememberChoices(key,ranked);
                        foreach(var alt in ranked.Where(x=>x.Quality=="Word")){
                            foreach(var item in doc.RootElement.EnumerateArray()){
                                if(GetInt(item,"id")!=alt.Id)continue;
                                var result=Decode(item,"LRCLIB");
                                if(result.Quality!="Word")continue;
                                token.ThrowIfCancellationRequested();
                                Remember(key,result);SetStatus(result);
                                return result;
                            }
                        }
                    }
                }
                // First usable line lyrics were shown immediately. Search the
                // free-text index for a legitimate word-timed version without
                // blocking the WPF render or inventing any timestamps.
                var broad=await SearchBroadLrclibAsync(title,artist,album,duration,
                    key,token,"Line");
                if(broad?.Quality=="Word"){
                    token.ThrowIfCancellationRequested();
                    Remember(key,broad);SetStatus(broad);
                    return broad;
                }
                return null;
            }finally{_requests.Release();}
        }catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch{return null;}
        finally{LastUpgradeMs=timer.Elapsed.TotalMilliseconds;}
    }
    private static TimeSpan? CueTime(Match tag,int offsetMs)
    {
        if(!int.TryParse(tag.Groups[1].Value,out int minutes)||
            !int.TryParse(tag.Groups[2].Value,out int seconds)||
            minutes>200||seconds>=60)return null;
        var fraction=tag.Groups[3].Value;
        int ms=fraction.Length switch {
            1=>int.Parse(fraction,CultureInfo.InvariantCulture)*100,
            2=>int.Parse(fraction,CultureInfo.InvariantCulture)*10,
            3=>int.Parse(fraction,CultureInfo.InvariantCulture),
            _=>0
        };
        return TimeSpan.FromMilliseconds(
            Math.Max(0,(minutes*60+seconds)*1000+ms+offsetMs));
    }
    public static IReadOnlyList<LyricLine> ParseLrc(string raw)
    {
        if(raw.Length>200_000)return Array.Empty<LyricLine>();
        var lines=new List<LyricLine>();
        int shift=0;
        foreach(var line in raw.Replace("\r","").Split('\n')){
            if(line.StartsWith("[offset:",StringComparison.OrdinalIgnoreCase)){
                var end=line.IndexOf(']');
                if(end>8&&int.TryParse(line.AsSpan(8,end-8),NumberStyles.Integer,
                    CultureInfo.InvariantCulture,out var offset))
                    shift=Math.Clamp(offset,-15_000,15_000);
            }
            var stamps=Stamp.Matches(line);
            if(stamps.Count==0)continue;
            string content=line[(stamps[^1].Index+stamps[^1].Length)..].Trim();
            var embedded=EnhancedStamp.Matches(content);
            string cleanText=embedded.Count==0?content:
                EnhancedStamp.Replace(content,"").Trim();
            if(cleanText.Length>400)cleanText=cleanText[..400];

            foreach(Match match in stamps){
                var at=CueTime(match,shift);
                if(at is null)continue;
                IReadOnlyList<LyricWord>? words=null;
                if(embedded is {Count:>0 and <=70} &&
                    string.IsNullOrWhiteSpace(content[..embedded[0].Index])){
                    var parsed=new List<LyricWord>();
                    bool valid=true;
                    for(int i=0;i<embedded.Count;i++){
                        var stamp=embedded[i];
                        var time=CueTime(stamp,shift);
                        int from=stamp.Index+stamp.Length;
                        int to=i+1<embedded.Count?embedded[i+1].Index:content.Length;
                        string part=content[from..to];
                        if(time is null||time<at||
                            (parsed.Count>0&&time<parsed[^1].Start)||
                            string.IsNullOrWhiteSpace(part)){
                            valid=false;break;
                        }
                        // Enhanced LRC provides actual word starts but no confirmed
                        // end of each word. Do NOT invent an ending timestamp.
                        parsed.Add(new LyricWord(time.Value,null,part));
                    }
                    if(valid && parsed.Count>0 &&
                        string.Equals(string.Concat(parsed.Select(x=>x.Text)).Trim(),
                            cleanText,StringComparison.Ordinal))
                        words=parsed;
                }
                lines.Add(new LyricLine(at.Value,cleanText,words));
                if(lines.Count>1500)return Array.Empty<LyricLine>();
            }
        }
        return lines.OrderBy(x=>x.Time).DistinctBy(x=>x.Time).ToArray();
    }
    public static int ActiveAt(IReadOnlyList<LyricLine> lines,TimeSpan current)
    {
        int lo=0,hi=lines.Count-1,found=-1;
        while(lo<=hi){
            int middle=lo+(hi-lo)/2;
            if(lines[middle].Time<=current){found=middle;lo=middle+1;}
            else hi=middle-1;
        }
        return found;
    }
    public void Dispose(){_http.Dispose();_requests.Dispose();}
}