using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CharliVision;

// Only user-supplied LRC/plaintext is saved. Third-party lyrics are kept in RAM.
public sealed class LyricsLibrary
{
    private sealed class State
    {
        public Dictionary<string,int> ChosenIds {get;set;}=new();
    }
    private readonly string _root;
    private readonly string _path;
    private State _state;
    public LyricsLibrary(string? root=null)
    {
        _root=root??Path.Combine(SettingsStore.DirectoryPath,"Lyrics");
        _path=Path.Combine(_root,"choices.json");
        try {
            _state=File.Exists(_path)?
                JsonSerializer.Deserialize<State>(File.ReadAllText(_path))??new():new();
            _state.ChosenIds??=new();
        }catch{_state=new State();}
    }
    public int SelectedId(string key)=>_state.ChosenIds.GetValueOrDefault(key);
    public void Select(string key,int id)
    {
        if(string.IsNullOrEmpty(key)||id<=0)return;
        if(_state.ChosenIds.Count>200&&!_state.ChosenIds.ContainsKey(key))
            _state.ChosenIds.Clear();
        _state.ChosenIds[key]=id;
        Save();
    }
    public void RemoveSelection(string key)
    {
        if(_state.ChosenIds.Remove(key))Save();
    }
    private static string SafeKey(string key)
    {
        if(key.Length!=24||!System.Text.RegularExpressions.Regex.IsMatch(key,"^[0-9A-F]{24}$"))
            throw new ArgumentException("Untrusted lyrics library key.");
        return key;
    }
    private string FileFor(string key)=>Path.Combine(_root,SafeKey(key)+".lrc");
    private string WordsFor(string key)=>Path.Combine(_root,SafeKey(key)+".words.yaml");
    public string? GetImportWords(string key)
    {
        try{
            var path=WordsFor(key);
            if(!File.Exists(path)||new FileInfo(path).Length>250_000)return null;
            return File.ReadAllText(path,Encoding.UTF8);
        }catch{return null;}
    }
    public void ImportWords(string key,string yaml)
    {
        if(string.IsNullOrWhiteSpace(yaml)||yaml.Length>250_000)
            throw new InvalidDataException("Generated words are empty or too large");
        var parsed=WordLyricsParser.ParseLyricsfile(yaml);
        if(parsed.Count==0||!System.Linq.Enumerable.Any(parsed,l=>l.Words is {Count:>0}))
            throw new InvalidDataException("Generated timestamps failed validation");
        Directory.CreateDirectory(_root);
        string path=WordsFor(key),tmp=path+".tmp";
        File.WriteAllText(tmp,yaml,new UTF8Encoding(false));
        File.Move(tmp,path,true);
    }
    public bool HasImport(string key)
    {
        try{return File.Exists(FileFor(key)) || File.Exists(WordsFor(key));}
        catch{return false;}
    }
    public string? GetImport(string key)
    {
        try {
            var path=FileFor(key);
            if(!File.Exists(path)||new FileInfo(path).Length>220_000)return null;
            return File.ReadAllText(path,Encoding.UTF8);
        }catch{return null;}
    }
    public void Import(string key,string content)
    {
        if(string.IsNullOrWhiteSpace(content)||content.Length>200_000)
            throw new InvalidDataException("Lyrics file is empty or too large.");
        Directory.CreateDirectory(_root);
        var destination=FileFor(key);
        var tmp=destination+".tmp";
        File.WriteAllText(tmp,content,new UTF8Encoding(false));
        File.Move(tmp,destination,true);
    }
    public void DeleteImport(string key)
    {
        try{
            File.Delete(FileFor(key));
            File.Delete(WordsFor(key));
        }catch(Exception ex){SettingsStore.Log(ex);}
    }
    private void Save()
    {
        try {
            Directory.CreateDirectory(_root);
            var tmp=_path+".tmp";
            File.WriteAllText(tmp,JsonSerializer.Serialize(_state,
                new JsonSerializerOptions{WriteIndented=true}));
            File.Move(tmp,_path,true);
        }catch(Exception ex){SettingsStore.Log(ex);}
    }
}