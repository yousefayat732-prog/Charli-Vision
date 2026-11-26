using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace CharliVision;

internal sealed class SignedVisionSource : IUpdateSource
{
#if VISION_UPDATE_QA
    internal const string AppId="CharliVision.V11.QA";
    private static string FeedDirectory=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharliVision-V11-QA-Feed");
    private IUpdateSource? _inner=new SimpleFileSource(new DirectoryInfo(FeedDirectory));
#else
    internal const string AppId="CharliVision.Desktop";
    private IUpdateSource? _inner;
#endif
    private const string LatestPage="https://github.com/yousefayat732-prog/Charli-Vision/releases/latest";
    private const string ReleasePrefix="https://github.com/yousefayat732-prog/Charli-Vision/releases/download/";
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(30)};
    private static readonly HttpClient RedirectHttp=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(15)};
    private VelopackAsset[] _trusted=Array.Empty<VelopackAsset>();

    internal static string StableReleaseBase(string? tag)
    {
        if(tag is null || !tag.StartsWith("v",StringComparison.Ordinal) ||
            !Version.TryParse(tag[1..],out var version) || version.Build<0 ||
            version.Revision!=-1 || tag!="v"+version.ToString(3))
            throw new InvalidDataException("Invalid stable release tag.");
        return ReleasePrefix+tag+"/";
    }
    internal static string StableRedirectTag(Uri? location)
    {
        var latest=new Uri(LatestPage);
        if(location is null)throw new InvalidDataException("Missing stable release redirect.");
        Uri target=location.IsAbsoluteUri?location:new Uri(latest,location);
        const string prefix="/yousefayat732-prog/Charli-Vision/releases/tag/";
        if(target.Scheme!=Uri.UriSchemeHttps ||
            !target.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) ||
            !target.IsDefaultPort || target.UserInfo.Length!=0 ||
            target.Query.Length!=0 || target.Fragment.Length!=0 ||
            !target.AbsolutePath.StartsWith(prefix,StringComparison.Ordinal))
            throw new InvalidDataException("Untrusted stable release redirect.");
        string tag=target.AbsolutePath[prefix.Length..];
        StableReleaseBase(tag);
        return tag;
    }
    internal static bool BelongsToRelease(VelopackAsset a,string tag)
    {
        string type=a.Type.ToString();
        return ValidAsset(a) && "v"+a.Version.ToString()==tag &&
            (type=="Full" || type=="Delta") &&
            a.FileName==AppId+"-"+a.Version+"-"+type.ToLowerInvariant()+".nupkg";
    }
    private static async Task<byte[]> ReadBounded(Stream input)
    {
        using var output=new MemoryStream();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] buffer=new byte[8192];int n;
        while((n=await input.ReadAsync(buffer.AsMemory(),timeout.Token))!=0){
            if(output.Length+n>262144)throw new InvalidDataException("Update metadata too large.");
            output.Write(buffer,0,n);
        }
        return output.ToArray();
    }
    private static async Task<byte[]> ReadUrl(string url)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.UserAgent.ParseAdd("CharliVision/1.7.4");
        request.Headers.CacheControl=new CacheControlHeaderValue{NoCache=true,MaxAge=TimeSpan.Zero};
        using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is long size && size>262144)
            throw new InvalidDataException("Update metadata too large.");
        using var input=await response.Content.ReadAsStreamAsync();
        return await ReadBounded(input);
    }
    private static async Task<(string Tag,string Base)> ResolveRelease()
    {
        using var request=new HttpRequestMessage(HttpMethod.Head,LatestPage);
        request.Headers.UserAgent.ParseAdd("CharliVision/1.7.4");
        request.Headers.CacheControl=new CacheControlHeaderValue{NoCache=true,MaxAge=TimeSpan.Zero};
        using var response=await RedirectHttp.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        int code=(int)response.StatusCode;
        if(code!=301 && code!=302 && code!=303 && code!=307 && code!=308)
            throw new InvalidDataException("No stable release redirect returned.");
        string tag=StableRedirectTag(response.Headers.Location);
        return (tag,StableReleaseBase(tag));
    }
    internal static bool ValidAsset(VelopackAsset a)=>
        a.PackageId==AppId && a.FileName==Path.GetFileName(a.FileName) &&
        a.FileName.StartsWith(AppId+"-",StringComparison.Ordinal) &&
        a.FileName.EndsWith(".nupkg",StringComparison.OrdinalIgnoreCase) &&
        a.Size>0 && a.Size<=650_000_000 &&
        a.SHA256 is {Length:64} && a.SHA256.All(Uri.IsHexDigit);
    private static bool Matches(VelopackAsset a,VelopackAsset b)=>
        ValidAsset(a) && ValidAsset(b) &&
        a.FileName==b.FileName && a.Version==b.Version && a.Type==b.Type &&
        a.Size==b.Size && string.Equals(a.SHA1,b.SHA1,StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SHA256,b.SHA256,StringComparison.OrdinalIgnoreCase);
    private static VelopackAssetFeed Authenticate(byte[] raw,byte[] signature)
    {
        if(!VisionUpdateService.VerifySignedManifest(raw,signature))
            throw new InvalidDataException("Update-Index-Signatur ungültig.");
        var signed=VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(raw));
        if(signed.Assets is null || signed.Assets.Length==0 || signed.Assets.Any(a=>!ValidAsset(a)))
            throw new InvalidDataException("Unzulässige signierte Pakete.");
        return signed;
    }
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,
        string? appId,string channel,Guid? stagingId=null,VelopackAsset? latestLocalRelease=null)
    {
        _trusted=Array.Empty<VelopackAsset>();
        if(appId!=AppId || channel!="win")
            throw new InvalidDataException("Unbekannte App oder Update-Kanal.");
        const string filename="releases.win.json";
#if VISION_UPDATE_QA
        byte[] raw=await File.ReadAllBytesAsync(Path.Combine(FeedDirectory,filename));
        byte[] signature=await File.ReadAllBytesAsync(Path.Combine(FeedDirectory,filename+".sig"));
        if(raw.Length>262144 || signature.Length>262144)throw new InvalidDataException("Update-Index zu gross.");
        var signed=Authenticate(raw,signature);
        var feed=await _inner!.GetReleaseFeed(logger,appId,channel,stagingId,latestLocalRelease);
        var verified=feed.Assets.Where(a=>signed.Assets.Any(s=>Matches(a,s))).ToArray();
        if(verified.Length==0)throw new InvalidDataException("Kein übereinstimmendes signiertes Paket.");
        foreach(var asset in verified){
            var authenticated=signed.Assets.First(s=>Matches(asset,s));
            asset.NotesMarkdown=authenticated.NotesMarkdown;
            asset.NotesHTML=authenticated.NotesHTML;
        }
        _trusted=verified;
        return new VelopackAssetFeed{Assets=verified};
#else
        _inner=null;
        Exception? last=null;
        for(int attempt=0;attempt<3;attempt++){
            try{
                var release=await ResolveRelease();
                var rawTask=ReadUrl(release.Base+filename);
                var signatureTask=ReadUrl(release.Base+filename+".sig");
                await Task.WhenAll(rawTask,signatureTask);
                var signed=Authenticate(await rawTask,await signatureTask);
                var verified=signed.Assets.Where(a=>BelongsToRelease(a,release.Tag)).ToArray();
                if(!verified.Any(a=>a.Type.ToString()=="Full" && "v"+a.Version.ToString()==release.Tag))
                    throw new InvalidDataException("Signiertes Vollpaket der Release-Version fehlt.");
                _inner=new SimpleWebSource(release.Base);
                _trusted=verified;
                return new VelopackAssetFeed{Assets=verified};
            }catch(Exception ex) when(ex is HttpRequestException or InvalidDataException or
                JsonException or TaskCanceledException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException){
                last=ex;
                if(attempt<2)await Task.Delay(TimeSpan.FromSeconds(attempt==0?1:3));
            }
        }
        throw new InvalidDataException("Release-Daten vorübergehend nicht verfügbar oder inkonsistent. Bitte erneut prüfen.",last);
#endif
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger,VelopackAsset entry,
        string localFile,Action<int> progress,CancellationToken cancelToken=default)
    {
        if(!_trusted.Any(a=>Matches(a,entry)))
            throw new InvalidDataException("Paket ist nicht im signierten Index.");
        try{
            await (_inner??throw new InvalidDataException("Keine authentifizierte Updatequelle."))
                .DownloadReleaseEntry(logger,entry,localFile,progress,cancelToken);
            using var stream=File.OpenRead(localFile);
            if(stream.Length!=entry.Size ||
                !Convert.ToHexString(await SHA256.HashDataAsync(stream,cancelToken))
                    .Equals(entry.SHA256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Paket-Prüfsumme ungültig.");
        }catch{try{File.Delete(localFile);}catch{}throw;}
    }
}
