using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace CharliVision;

/// <summary>Authenticate the complete channel index before handing assets to Velopack.
/// The SDK still handles full/delta selection, installation and restart.</summary>
internal sealed class SignedVisionSource : IUpdateSource
{
#if VISION_UPDATE_QA
    internal const string AppId="CharliVision.V11.QA";
    private static string FeedDirectory=>Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharliVision-V11-QA-Feed");
    private readonly IUpdateSource _inner=new SimpleFileSource(new DirectoryInfo(FeedDirectory));
#else
    internal const string AppId="CharliVision.Desktop";
    private const string BaseUrl=
        "https://github.com/yousefayat732-prog/Charli-Vision/releases/latest/download/";
    private readonly IUpdateSource _inner=new GithubSource(
        "https://github.com/yousefayat732-prog/Charli-Vision",null,false);
#endif
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromSeconds(30)};
    private VelopackAsset[] _trusted=Array.Empty<VelopackAsset>();
    private static async Task<byte[]> ReadMetadata(string filename)
    {
#if VISION_UPDATE_QA
        byte[] bytes=await File.ReadAllBytesAsync(Path.Combine(FeedDirectory,filename));
        if(bytes.Length>262144)throw new InvalidDataException("Update-Index zu gross.");
        return bytes;
#else
        using var response=await Http.GetAsync(BaseUrl+filename,HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is long size && size>262144)
            throw new InvalidDataException("Update-Index zu gross.");
        using var input=await response.Content.ReadAsStreamAsync();
        using var output=new MemoryStream();
        byte[] buffer=new byte[8192];int n;
        while((n=await input.ReadAsync(buffer))!=0){
            if(output.Length+n>262144)throw new InvalidDataException("Update-Index zu gross.");
            output.Write(buffer,0,n);
        }
        return output.ToArray();
#endif
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
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger,
        string? appId,string channel,Guid? stagingId=null,VelopackAsset? latestLocalRelease=null)
    {
        _trusted=Array.Empty<VelopackAsset>();
        if(appId!=AppId || channel!="win")
            throw new InvalidDataException("Unbekannte App oder Update-Kanal.");
        const string filename="releases.win.json";
        byte[] raw=await ReadMetadata(filename);
        byte[] signature=await ReadMetadata(filename+".sig");
        if(!VisionUpdateService.VerifySignedManifest(raw,signature))
            throw new InvalidDataException("Update-Index-Signatur ungültig.");
        var signed=VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(raw));
        if(signed.Assets is null || signed.Assets.Length==0 ||
            signed.Assets.Any(a=>!ValidAsset(a)))
            throw new InvalidDataException("Unzulässige signierte Pakete.");
        var feed=await _inner.GetReleaseFeed(logger,appId,channel,stagingId,latestLocalRelease);
        var verified=feed.Assets.Where(a=>signed.Assets.Any(s=>Matches(a,s))).ToArray();
        if(verified.Length==0)
            throw new InvalidDataException("Kein übereinstimmendes signiertes Paket.");
        foreach(var asset in verified){
            var authenticated=signed.Assets.First(s=>Matches(asset,s));
            asset.NotesMarkdown=authenticated.NotesMarkdown;
            asset.NotesHTML=authenticated.NotesHTML;
        }
        _trusted=verified;
        return new VelopackAssetFeed{Assets=verified};
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger,VelopackAsset entry,
        string localFile,Action<int> progress,CancellationToken cancelToken=default)
    {
        if(!_trusted.Any(a=>Matches(a,entry)))
            throw new InvalidDataException("Paket ist nicht im signierten Index.");
        try{
            await _inner.DownloadReleaseEntry(logger,entry,localFile,progress,cancelToken);
            using var stream=File.OpenRead(localFile);
            if(stream.Length!=entry.Size ||
                !Convert.ToHexString(await SHA256.HashDataAsync(stream,cancelToken))
                    .Equals(entry.SHA256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Paket-Prüfsumme ungültig.");
        }catch{try{File.Delete(localFile);}catch{}throw;}
    }
}
