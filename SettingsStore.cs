using System;
using System.IO;
using System.Text.Json;

namespace CharliVision;

public sealed class AppSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Scale { get; set; } = 1;
    public bool AlwaysOnTop { get; set; }
    public bool ClickThrough { get; set; }
    public bool AutoStart { get; set; }
    public bool AutomaticUpdateChecks { get; set; }=true;
    public bool ReducedMotion { get; set; }
    // Opt-in only; an unset default doesn't rewrite existing settings files.
    [System.Text.Json.Serialization.JsonIgnore(
        Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PerformanceAutopilot { get; set; }=false;
    public string RenderProfile { get; set; }="Balanced";
    public string VinylQuality { get; set; }="Balanced";
    public bool CoverRotates { get; set; }=true;
    public bool AutoLyricsSearch { get; set; }=true;
    public bool PlainLyricsReader { get; set; }=true;
    // Migrates legacy installations that previously hid all unsynced lyrics.
    public int LyricsDisplayVersion { get; set; }=0;
    public bool VinylCenterTransparent { get; set; }=true;
    public bool GlobalHotkeysEnabled { get; set; }=false;
    public System.Collections.Generic.Dictionary<string,string> Hotkeys { get; set; }=new();
    public bool AlbumColorsEnabled { get; set; }=true;
    public bool PositionLocked { get; set; }=false;
    public bool WordAnimationEnabled { get; set; }=true;
    public bool VinylRotationEnabled { get; set; }=true;
    public bool AdaptiveVinylTempoEnabled { get; set; }=true;
    public double VinylBaseRpm { get; set; }=33.333;
    public double VinylScale { get; set; }=1;
    public double LyricsScale { get; set; }=1;
    public double LyricsSpacing { get; set; }=0;
    public double AudioSensitivity { get; set; }=1;
    public double AudioSmoothing { get; set; }=1;
    public double LyricsGlow { get; set; }=.55;
    public bool HighContrastLyrics { get; set; }=false;
}
public static class SettingsStore
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CharliVision");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try {
            if (File.Exists(FilePath))
                return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings());
        } catch(Exception ex) {
            Log(new InvalidDataException(
                "Einstellungen konnten nicht geladen werden. Sichere Standardwerte werden verwendet.",ex));
        }
        return Normalize(new AppSettings());
    }
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Hotkeys??=new();
        if(settings.LyricsDisplayVersion<1){
            // New policy: show unsynchronized lyrics as clearly marked reading text.
            settings.PlainLyricsReader=true;
            settings.LyricsDisplayVersion=1;
        }
        settings.Scale=Math.Clamp(settings.Scale,.65,1.65);
        settings.VinylScale=Math.Clamp(settings.VinylScale,.7,1.18);
        settings.LyricsScale=Math.Clamp(settings.LyricsScale,.7,1.4);
        settings.LyricsSpacing=Math.Clamp(settings.LyricsSpacing,0,70);
        settings.VinylBaseRpm=double.IsFinite(settings.VinylBaseRpm)?
            Math.Clamp(settings.VinylBaseRpm,24,52):33.333;
        settings.AudioSensitivity=Math.Clamp(settings.AudioSensitivity,.5,2.5);
        settings.AudioSmoothing=Math.Clamp(settings.AudioSmoothing,.5,2.5);
        settings.LyricsGlow=Math.Clamp(settings.LyricsGlow,0,1);
        return settings;
    }
    public static void Save(AppSettings settings)
    {
        try {
            Directory.CreateDirectory(DirectoryPath);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        } catch(Exception ex) {
            Log(new IOException("Einstellungen konnten nicht gespeichert werden.",ex));
        }
    }
    public static void Log(Exception exception)
    {
        try {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath,"errors.log"),
                DateTimeOffset.Now.ToString("u") + "  " + exception + Environment.NewLine);
        } catch { }
    }
}