using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace CharliVision;

/// <summary>Positive offset advances lyrics; negative offset delays them.</summary>
public sealed class LyricsSyncSettings
{
    private sealed class Model
    {
        public int GlobalMs {get;set;}
        public Dictionary<string,int> Songs {get;set;}=new();
        public Dictionary<string,int> Devices {get;set;}=new();
    }
    private readonly string _path;
    private Model _model;
    public LyricsSyncSettings(string? filePath=null)
    {
        _path=filePath??Path.Combine(SettingsStore.DirectoryPath,"lyrics-sync.json");
        try {
            _model=File.Exists(_path)?
                JsonSerializer.Deserialize<Model>(File.ReadAllText(_path))??new():new();
            _model.Songs??=new();_model.Devices??=new();
        }catch{_model=new Model();}
        _model.GlobalMs=Clamp(_model.GlobalMs);
    }
    private static int Clamp(int n)=>Math.Clamp(n,-5000,5000);
    private static string Key(string text)
    {
        var data=SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormKC)));
        return Convert.ToHexString(data)[..24];
    }
    public static string SongKey(string title,string artist,string album,TimeSpan? duration)
    {
        string normalize(string t)=>t.Trim().ToUpperInvariant();
        return Key(normalize(title)+"\n"+normalize(artist)+"\n"+
            normalize(album)+"\n"+(int)Math.Round(duration?.TotalSeconds??0));
    }
    public static string? DeviceKey()
    {
        try {
            using var devices=new MMDeviceEnumerator();
            using var device=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
            return Key(device.ID);
        }catch{return null;}
    }
    public int GlobalMs=>_model.GlobalMs;
    public int SongMs(string key)=>_model.Songs.GetValueOrDefault(key);
    public int DeviceMs(string? key)=>key is null?0:_model.Devices.GetValueOrDefault(key);
    public int EffectiveMs(string song,string? device) =>
        Clamp(GlobalMs+SongMs(song)+DeviceMs(device));
    public TimeSpan? Apply(TimeSpan? playback,string song,string? device)
    {
        if(playback is null)return null;
        return TimeSpan.FromTicks(Math.Max(0,
            playback.Value.Ticks+TimeSpan.FromMilliseconds(EffectiveMs(song,device)).Ticks));
    }
    public void AdjustGlobal(int delta)
    {
        _model.GlobalMs=Clamp(_model.GlobalMs+delta);Save();
    }
    public void AdjustSong(string key,int delta)
    {
        if(string.IsNullOrEmpty(key))return;
        int updated=Clamp(SongMs(key)+delta);
        if(updated==0)_model.Songs.Remove(key);
        else {
            if(_model.Songs.Count>=250 && !_model.Songs.ContainsKey(key))_model.Songs.Clear();
            _model.Songs[key]=updated;
        }
        Save();
    }
    public void AdjustDevice(string? key,int delta)
    {
        if(key is null)return;
        int updated=Clamp(DeviceMs(key)+delta);
        if(updated==0)_model.Devices.Remove(key);
        else {_model.Devices[key]=updated;}
        Save();
    }
    public void ResetSong(string key){_model.Songs.Remove(key);Save();}
    public void ResetGlobal(){_model.GlobalMs=0;Save();}
    public void ResetDevice(string? key){if(key is not null){_model.Devices.Remove(key);Save();}}
    private void Save()
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)??SettingsStore.DirectoryPath);
            var tmp=_path+".tmp";
            File.WriteAllText(tmp,JsonSerializer.Serialize(_model,
                new JsonSerializerOptions {WriteIndented=true}));
            File.Move(tmp,_path,true);
        }catch(Exception ex){SettingsStore.Log(ex);}
    }
}