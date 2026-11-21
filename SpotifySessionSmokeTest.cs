using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace CharliVision;

// One-shot opt-in diagnostic using the SAME Windows Spotify media session.
// Does not inspect credentials, cookies, or Spotify's protected audio.
public static class SpotifySessionSmokeTest
{
    public static bool Run(bool resume=false,bool skipAndRestore=false)
    {
        var rows=new List<string>{
            "CHARLI VISION — SPOTIFY MEDIA SESSION LIVE",
            DateTimeOffset.Now.ToString("u")
        };
        bool okay=false;
        try {
            okay=Task.Run(async ()=>{
                var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session=manager.GetSessions().FirstOrDefault(x=>
                    x.SourceAppUserModelId.Contains("spotify",StringComparison.OrdinalIgnoreCase));
                if(session is null){rows.Add("BLOCKED: No Spotify media session");return false;}
                var media=await session.TryGetMediaPropertiesAsync();
                var before=media?.Title??"";
                var artist=media?.Artist??"";
                var info=session.GetPlaybackInfo();
                var playing=info?.PlaybackStatus==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                rows.Add("TITLE="+before);
                rows.Add("ARTIST="+artist);
                rows.Add("BEFORE_PLAYING="+playing);
                if(resume && !playing){
                    bool command=await session.TryPlayAsync();
                    await Task.Delay(1200);
                    playing=session.GetPlaybackInfo()?.PlaybackStatus==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    rows.Add("RESUME_ACCEPTED="+command);
                    rows.Add("AFTER_PLAYING="+playing);
                }
                if(skipAndRestore){
                    bool moved=await session.TrySkipNextAsync();
                    await Task.Delay(1800);
                    var next=await session.TryGetMediaPropertiesAsync();
                    rows.Add("NEXT_ACCEPTED="+moved);
                    rows.Add("NEXT_TITLE="+(next?.Title??""));
                    if(moved && next?.Title!=before){
                        bool previous=await session.TrySkipPreviousAsync();
                        await Task.Delay(1500);
                        rows.Add("RESTORE_ACCEPTED="+previous);
                        var restored=await session.TryGetMediaPropertiesAsync();
                        rows.Add("RESTORED_TITLE="+restored?.Title);
                    }
                }
                var latest=await session.TryGetMediaPropertiesAsync();
                var clock=session.GetTimelineProperties();
                rows.Add("CLOCK_POSITION_MS="+clock.Position.TotalMilliseconds.ToString("0"));
                rows.Add("TRACK_TITLE="+(latest?.Title??""));
                var duration=clock.EndTime-clock.StartTime;
                rows.Add("TRACK_DURATION_MS="+duration.TotalMilliseconds.ToString("0"));
                // Diagnose the provider for the actual active recording; never log lyrics text.
                if(latest is not null && !string.IsNullOrWhiteSpace(latest.Title) &&
                    !string.IsNullOrWhiteSpace(latest.Artist)){
                    try{
                        using var lyrics=new LyricsService();
                        using var deadline=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                        var result=await lyrics.GetAsync(latest.Title,latest.Artist,
                            latest.AlbumTitle??"",duration>TimeSpan.Zero?duration:null,deadline.Token);
                        rows.Add("LYRICS_SOURCE="+result.Source);
                        rows.Add("LYRICS_QUALITY="+result.Quality);
                        rows.Add("LYRICS_LINES="+result.Lines.Count);
                        rows.Add("LYRICS_PLAIN_CHARS="+(result.PlainText?.Length??0));
                        rows.Add("LYRICS_STATUS="+result.Status);
                    }catch(Exception ex){rows.Add("LYRICS_ERROR="+ex.GetType().Name);}
                }
                return playing && latest is not null && clock.EndTime>clock.StartTime;
            }).GetAwaiter().GetResult();
        }catch(Exception ex){
            rows.Add("ERROR="+ex.GetType().Name+": "+ex.Message);
            SettingsStore.Log(ex);
        }
        rows.Add("RESULT="+(okay?"PASS":"BLOCKED_OR_FAILED"));
        try {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllLines(Path.Combine(SettingsStore.DirectoryPath,
                "spotify-session-test.txt"),rows);
        }catch{}
        return okay;
    }
}