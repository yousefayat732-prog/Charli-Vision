using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace CharliVision;

/// <summary>
/// GitHub release updates backed by the project's own RSA-PSS public key.
/// Unsigned releases, unapproved hosts, and hash mismatches NEVER execute.
/// Automatic checking is opt-in by user settings; installation is always manual.
/// </summary>
public sealed class VisionUpdateService
{
    public static VisionUpdateService Shared {get;}=new();
    private const string ReleasesApi=
        "https://api.github.com/repos/yousefayat732-prog/Charli-Vision/releases/latest";
    private const string ReleaseAssetPrefix=
        "/yousefayat732-prog/Charli-Vision/releases/download/";
    private const string ManifestName="CharliVision-update.json";
    private const string SignatureName="CharliVision-update.sig";
    private const long MaximumInstallerSize=650_000_000;
    // Public verifier only. The private signing key MUST NEVER be shipped or committed.
    private const string PublicKey=@"-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAyOhzPQlBfUd89HCOsa3q
fGfQ436K+4D8irm8L/02jOHrhyIuaUyTb0ZWAxOfcdIC+FVs0NW13RZ31q1qHa3S
Og7wYrDts0sDOT0f/2idK7o8VhfeEf6dEwgEX/p5f1zPnk5H6bXcrzQKdh3aLLE/
lu7Upp/dFGMqlRbUjvy7xFnL3HCT2KqEib4jGFF0H33oKw85M1eG03BF8V9NY9PG
F6jdViLe97ys5VpRWU1uhB+pFBioBuTJBuS8JbHvnqBKYkeqyglBMThv/BYxndTn
t63cW1FZBqUVL33LNRrhuFYvlSMQGpDLQrD5zWUXDw1qap1v/4M5QKnMh5NEpkeO
7X0dF5j2lyaziA1nQXKQnCE4y+kr9OgqCJmetpzXCAbNsZoe2D9YKp6/a/f42aZy
+TK83X9lJlgHvrvmGPDcBuC89fKfV5JDvYk/tYOxKZuLWu6O6P8n/QKbq7w613TT
BiQPQcqQ6AzDvc1/db5JkFTDPzpIZFQcdSUqh4byR8EhAgMBAAE=
-----END PUBLIC KEY-----";
    private readonly HttpClient _http=new() {Timeout=TimeSpan.FromMinutes(15)};
    private readonly SemaphoreSlim _gate=new(1,1);
    private UpdateInfo? _update;
    private UpdateManager? _manager;
    private DateTime _lastChecked=DateTime.MinValue;
    public string InstalledVersion =>
        typeof(VisionUpdateService).Assembly.GetName().Version?.ToString(3)??"1.0.0";
    public string AvailableVersion {get;private set;}="—";
    public string ReleaseNotes {get;private set;}="";
    public string Status {get;private set;}="Noch nicht nach Updates gesucht";
    public bool CanInstall => _update is not null;
    public long InstallerBytes => _update?.TargetFullRelease.Size??0;

    private sealed record Manifest(string Version,string FileName,string Sha256,long Size);
    private sealed record Offer(Manifest Manifest,Uri Url);
    public static bool IsNewer(string current,string candidate)
    {
        if(!Version.TryParse(current.TrimStart('v','V'),out var oldVersion) ||
           !Version.TryParse(candidate.TrimStart('v','V'),out var nextVersion))
            return false;
        return nextVersion>oldVersion;
    }
    private static Uri VerifiedAssetUrl(JsonElement asset,string expectedFile)
    {
        if(!asset.TryGetProperty("name",out var assetName) ||
           assetName.GetString()!=expectedFile ||
           !asset.TryGetProperty("browser_download_url",out var urlText) ||
           !Uri.TryCreate(urlText.GetString(),UriKind.Absolute,out var url) ||
           url.Scheme!=Uri.UriSchemeHttps ||
           !url.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) ||
           !url.AbsolutePath.StartsWith(ReleaseAssetPrefix,
               StringComparison.OrdinalIgnoreCase) ||
           !url.AbsolutePath.EndsWith("/"+expectedFile,StringComparison.Ordinal))
            throw new InvalidDataException("Ungültiges Release-Asset.");
        return url;
    }
    private static Uri FindAsset(JsonElement assets,string filename)
    {
        foreach(var asset in assets.EnumerateArray())
            if(asset.TryGetProperty("name",out var name) &&
               name.GetString()==filename)
                return VerifiedAssetUrl(asset,filename);
        throw new InvalidDataException("Release-Asset fehlt: "+filename);
    }
    private async Task<byte[]> SmallDownload(Uri url,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.UserAgent.ParseAdd("CharliVision/1.0");
        using var response=await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead,token);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is long length && length>262144)
            throw new InvalidDataException("Release-Metadaten zu groß.");
        using var stream=await response.Content.ReadAsStreamAsync(token);
        using var memory=new MemoryStream();
        var buffer=new byte[8192];
        int read;
        while((read=await stream.ReadAsync(buffer,token))!=0){
            if(memory.Length+read>262144)
                throw new InvalidDataException("Release-Metadaten zu groß.");
            memory.Write(buffer,0,read);
        }
        return memory.ToArray();
    }
    internal static bool VerifySignedManifest(byte[] manifest,byte[] signatureBytes)
    {
        try{
            byte[] signature=Convert.FromBase64String(
                Encoding.ASCII.GetString(signatureBytes).Trim());
            using var rsa=RSA.Create();
            rsa.ImportFromPem(PublicKey);
            return rsa.VerifyData(manifest,signature,
                HashAlgorithmName.SHA256,RSASignaturePadding.Pss);
        }catch(CryptographicException){return false;}
        catch(FormatException){return false;}
    }
    public async Task CheckAsync(bool force=false,CancellationToken token=default)
    {
        if(force)await _gate.WaitAsync(token);
        else if(!await _gate.WaitAsync(0,token))return;
        try{
            if(!force && DateTime.UtcNow-_lastChecked<TimeSpan.FromHours(18))return;
            _update=null;AvailableVersion="—";ReleaseNotes="";
            Status="Signierten Velopack-Updatekanal prüfen …";
            var manager=new UpdateManager(new SignedVisionSource());
            if(!manager.IsInstalled || manager.AppId!=SignedVisionSource.AppId){
                Status="Einmalige Velopack-Migration erforderlich.";
                return;
            }
            token.ThrowIfCancellationRequested();
            var found=await manager.CheckForUpdatesAsync();
            token.ThrowIfCancellationRequested();
            _lastChecked=DateTime.UtcNow;
            if(found is null){Status="Aktuell — "+InstalledVersion;return;}
            if(!SignedVisionSource.ValidAsset(found.TargetFullRelease) ||
                !IsNewer(InstalledVersion,found.TargetFullRelease.Version.ToString()))
                throw new InvalidDataException("Nicht zugelassenes Update.");
            _manager=manager;_update=found;
            AvailableVersion=found.TargetFullRelease.Version.ToString();
            ReleaseNotes=found.TargetFullRelease.NotesMarkdown??"";
            if(ReleaseNotes.Length>1600)ReleaseNotes=ReleaseNotes[..1600];
            Status="Update verfügbar — Signatur verifiziert: "+AvailableVersion;
        }catch(Exception ex) when(ex is not OperationCanceledException){
            _update=null;_lastChecked=DateTime.UtcNow.AddHours(-17);
            Status="Updateprüfung fehlgeschlagen: "+ex.Message;
            SettingsStore.Log(ex);
        }finally{_gate.Release();}
    }
    public async Task<bool> DownloadAndStartInstallerAsync(
        IProgress<(long received,long total)>? progress=null,
        CancellationToken token=default)
    {
        await _gate.WaitAsync(token);
        try{
            if(_update is not UpdateInfo update || _manager is not UpdateManager manager)
                return false;
            Status="Signierte Updatepakete werden geladen …";
            await manager.DownloadUpdatesAsync(update,
                p=>progress?.Report((Math.Clamp(p,0,100),100)),token);
            token.ThrowIfCancellationRequested();
            Status="Updatepaket geprüft — Neustart wird vorbereitet";
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease,
                silent:false,restart:true);
            return true;
        }catch(Exception ex) when(ex is not OperationCanceledException){
            Status="Update fehlgeschlagen: "+ex.Message;
            SettingsStore.Log(ex);return false;
        }finally{_gate.Release();}
    }
}