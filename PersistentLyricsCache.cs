using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CharliVision;

/// <summary>Private, bounded cache of validated LRCLIB matches only.
/// No uploads, no negative entries, no guessed recording matches.</summary>
internal sealed class PersistentLyricsCache
{
    private sealed record Entry(int Schema,string Key,DateTimeOffset Saved,LyricResult Result);
    private readonly string _root;
    private readonly object _gate=new();
    private static readonly TimeSpan Lifetime=TimeSpan.FromDays(30);
    private const long MaxBytes=1_000_000;
    private const int MaxEntries=200;
    public PersistentLyricsCache(string root)=>_root=root;
    private static bool KeyValid(string key)=>key.Length==24 &&
        key.All(Uri.IsHexDigit);
    private string FileFor(string key)=>Path.Combine(_root,key+".json");
    internal static bool Eligible(LyricResult value)=>
        (value.Source=="LRCLIB" || value.Source=="LRCLIB (gewählt)" ||
         value.Source=="LRCLIB (erweiterte Suche)") &&
        ((value.Synchronized && value.Quality is "Word" or "Line" &&
            value.Lines.Count>0 && value.Lines.Count<=3000 &&
            value.Lines.All(l=>l.Time>=TimeSpan.Zero && l.Time<=TimeSpan.FromHours(3) &&
                l.Text.Length<=10000)) ||
         (!value.Synchronized && value.Quality=="Plain" &&
            !string.IsNullOrWhiteSpace(value.PlainText) && value.PlainText.Length<=70000));
    public LyricResult? Read(string key)
    {
        if(!KeyValid(key))return null;
        lock(_gate)try{
            string path=FileFor(key);
            if(!File.Exists(path)||new FileInfo(path).Length>MaxBytes)return null;
            var item=JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path));
            var now=DateTimeOffset.UtcNow;
            return item is not null && item.Schema==1 && item.Key==key &&
                item.Saved<=now.AddMinutes(5) && now-item.Saved<=Lifetime &&
                Eligible(item.Result)?item.Result:null;
        }catch{return null;}
    }
    public void Write(string key,LyricResult value)
    {
        if(!KeyValid(key)||!Eligible(value))return;
        lock(_gate)try{
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(
                new Entry(1,key,DateTimeOffset.UtcNow,value));
            if(bytes.Length>MaxBytes)return;
            Directory.CreateDirectory(_root);
            string target=FileFor(key),tmp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(tmp,bytes);File.Move(tmp,target,true);}
            finally{if(File.Exists(tmp))File.Delete(tmp);}
            var files=new DirectoryInfo(_root).EnumerateFiles("*.json")
                .Where(f=>KeyValid(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
            foreach(var file in files.Where((f,i)=>i>=MaxEntries ||
                DateTime.UtcNow-f.LastWriteTimeUtc>Lifetime))file.Delete();
        }catch{/* cache failure cannot prevent playback */}
    }
    public void Remove(string key)
    {
        if(!KeyValid(key))return;
        lock(_gate)try{File.Delete(FileFor(key));}catch{}
    }
}
