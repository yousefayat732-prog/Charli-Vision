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
    private Offer? _offer;
    private DateTime _lastChecked=DateTime.MinValue;
    public string InstalledVersion =>
        typeof(VisionUpdateService).Assembly.GetName().Version?.ToString(3)??"1.0.0";
    public string AvailableVersion {get;private set;}="—";
    public string ReleaseNotes {get;private set;}="";
    public string Status {get;private set;}="Noch nicht nach Updates gesucht";
    public bool CanInstall => _offer is not null;
    public long InstallerBytes => _offer?.Manifest.Size??0;

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
    private static bool VerifySignedManifest(byte[] manifest,byte[] signatureBytes)
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
        if(!await _gate.WaitAsync(0,token))return;
        try{
            if(!force && DateTime.UtcNow-_lastChecked<TimeSpan.FromHours(18))
                return;
            _lastChecked=DateTime.UtcNow;
            _offer=null;
            AvailableVersion="—";
            ReleaseNotes="";
            Status="Suche nach signierten GitHub-Updates …";
            using var request=new HttpRequestMessage(HttpMethod.Get,ReleasesApi);
            request.Headers.UserAgent.ParseAdd("CharliVision/1.0");
            using var response=await _http.SendAsync(request,token);
            if(response.StatusCode==HttpStatusCode.NotFound){
                Status="Update-Kanal noch nicht veröffentlicht.";
                return;
            }
            response.EnsureSuccessStatusCode();
            var bytes=await response.Content.ReadAsByteArrayAsync(token);
            if(bytes.Length>262144)
                throw new InvalidDataException("Release-Antwort zu groß.");
            using var document=JsonDocument.Parse(bytes);
            var root=document.RootElement;
            var tag=root.GetProperty("tag_name").GetString()??"";
            var version=tag.TrimStart('v','V');
            var assets=root.GetProperty("assets");
            if(!IsNewer(InstalledVersion,version)){
                Status="Aktuell — Version "+InstalledVersion;
                return;
            }
            var manifestUrl=FindAsset(assets,ManifestName);
            var sigUrl=FindAsset(assets,SignatureName);
            var raw=await SmallDownload(manifestUrl,token);
            var signature=await SmallDownload(sigUrl,token);
            if(!VerifySignedManifest(raw,signature))
                throw new InvalidDataException("Ungültige Release-Signatur.");
            var manifest=JsonSerializer.Deserialize<Manifest>(raw,
                new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            if(manifest is null ||
               manifest.Version!=version ||
               !IsNewer(InstalledVersion,manifest.Version) ||
               manifest.Size<1 || manifest.Size>MaximumInstallerSize ||
               manifest.Sha256.Length!=64 ||
               !manifest.Sha256.All(Uri.IsHexDigit) ||
               !manifest.FileName.Equals(
                    $"CharliVision-{version}-Setup-x64.exe",StringComparison.Ordinal))
                throw new InvalidDataException("Unzulässiges signiertes Update.");
            var packageUrl=FindAsset(assets,manifest.FileName);
            _offer=new Offer(manifest,packageUrl);
            AvailableVersion=version;
            ReleaseNotes=root.TryGetProperty("body",out var body)
                ?(body.GetString()??"")[..Math.Min(1600,(body.GetString()??"").Length)]:"";
            Status="Update verfügbar — verifiziert: "+version;
        }
        catch(Exception ex) when(ex is not OperationCanceledException){
            _offer=null;
            Status="Updateprüfung fehlgeschlagen: "+ex.Message;
            SettingsStore.Log(ex);
        }
        finally{_gate.Release();}
    }
    public async Task<bool> DownloadAndStartInstallerAsync(
        IProgress<(long received,long total)>? progress=null,
        CancellationToken token=default)
    {
        if(_offer is not Offer offer)return false;
        await _gate.WaitAsync(token);
        string? temporary=null;
        try{
            Status="Wird heruntergeladen …";
            string directory=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CharliVision","Updates",offer.Manifest.Version);
            Directory.CreateDirectory(directory);
            string target=Path.Combine(directory,offer.Manifest.FileName);
            temporary=target+".part";
            using var request=new HttpRequestMessage(HttpMethod.Get,offer.Url);
            request.Headers.UserAgent.ParseAdd("CharliVision/1.0");
            using var response=await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            using var input=await response.Content.ReadAsStreamAsync(token);
            using var output=new FileStream(temporary,FileMode.Create,
                FileAccess.Write,FileShare.None,81920,true);
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer=new byte[81920];
            long total=0;
            int n;
            while((n=await input.ReadAsync(buffer,token))>0){
                total+=n;
                if(total>offer.Manifest.Size)
                    throw new InvalidDataException("Download größer als Freigabe.");
                await output.WriteAsync(buffer.AsMemory(0,n),token);
                hash.AppendData(buffer,0,n);
                progress?.Report((total,offer.Manifest.Size));
            }
            await output.FlushAsync(token);
            if(total!=offer.Manifest.Size ||
               !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                    offer.Manifest.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer-Prüfsumme falsch.");
            output.Close();
            File.Move(temporary,target,true);
            temporary=null;
            Status="Verifiziert — Installation wird gestartet";
            _ = Process.Start(new ProcessStartInfo{
                FileName=target,UseShellExecute=true,
                Arguments="/NORESTART"
            }) ?? throw new InvalidOperationException("Installer konnte nicht starten.");
            return true;
        }catch(Exception ex) when(ex is not OperationCanceledException){
            Status="Update fehlgeschlagen: "+ex.Message;
            SettingsStore.Log(ex);
            return false;
        }finally{
            if(temporary is not null)
                try{File.Delete(temporary);}catch{}
            _gate.Release();
        }
    }
}
