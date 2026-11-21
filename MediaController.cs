using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CharliVision;

public sealed record TrackState(string Title,string Artist,string Album,
    bool Playing,BitmapSource? Cover,TimeSpan? Duration);

// Immutable raw media sample; remains usable in existing deterministic tests.
public sealed record PlaybackClock(TimeSpan Position,TimeSpan Duration,
    DateTimeOffset LastUpdated,bool Playing,bool Valid)
{
    public TimeSpan? Estimated()
    {
        if(!Valid || Duration<=TimeSpan.Zero)return null;
        var age=DateTimeOffset.UtcNow-LastUpdated;
        if(Playing && (age<TimeSpan.FromSeconds(-3)||age>TimeSpan.FromMinutes(2)))return null;
        var value=Position+(Playing?TimeSpan.FromSeconds(Math.Max(0,age.TotalSeconds)):TimeSpan.Zero);
        return TimeSpan.FromTicks(Math.Clamp(value.Ticks,0,Duration.Ticks));
    }
}
public sealed class MediaController : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _trackKey="";
    private BitmapSource? _cover;
    private bool _lastPlaying,_hadSpotify;
    private volatile bool _needsDiscovery=true,_needsMetadata=true;
    private long _lastDiscovery,_lastMetadata,_lastRequested;
    private long _retryAfter;
    private bool _disposed;
    public PrecisionPlaybackClock Clock { get; }=new();
    public event Action<TrackState>? Changed;
    public event Action? RefreshRequested;
    public double LastMetadataQueryMs {get;private set;}
    public double LastPollMs {get;private set;}
    public double LastRecognitionDelayMs {get;private set;}

    private void ManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs e) { _needsDiscovery=true; Notify(); }
    private void MediaChanged(GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs e) { _needsMetadata=true; Notify(); }
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs e) { Notify(); }
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs e) { Notify(); }

    private void Notify()
    {
        var now=Stopwatch.GetTimestamp();
        var previous=Interlocked.Read(ref _lastRequested);
        if((now-previous)/(double)Stopwatch.Frequency<.08)return;
        Interlocked.Exchange(ref _lastRequested,now);
        RefreshRequested?.Invoke();
    }
    private void Unsubscribe()
    {
        if(_session is not null) {
            try{
                _session.MediaPropertiesChanged-=MediaChanged;
                _session.PlaybackInfoChanged-=PlaybackChanged;
                _session.TimelinePropertiesChanged-=TimelineChanged;
            }catch{}
        }
        _session=null;
    }
    private void SetSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if(ReferenceEquals(_session,session))return;
        Unsubscribe();
        _session=session;_needsMetadata=true;_trackKey="";
        Clock.Reset();
        if(session is not null) {
            session.MediaPropertiesChanged+=MediaChanged;
            session.PlaybackInfoChanged+=PlaybackChanged;
            session.TimelinePropertiesChanged+=TimelineChanged;
        }
    }
    public async Task PollAsync()
    {
        if(_disposed || Stopwatch.GetTimestamp()<_retryAfter)return;
        long began=Stopwatch.GetTimestamp();
        try {
            if(_manager is null) {
                _manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged+=ManagerChanged;
                _needsDiscovery=true;
            }
            long stamp=Stopwatch.GetTimestamp();
            if(_needsDiscovery || (stamp-_lastDiscovery)/(double)Stopwatch.Frequency>2.5) {
                _lastDiscovery=stamp;_needsDiscovery=false;
                var spotify=_manager.GetSessions().FirstOrDefault(s=>
                    s.SourceAppUserModelId.Contains("spotify",StringComparison.OrdinalIgnoreCase));
                if(_session is null || spotify is null ||
                    _session.SourceAppUserModelId!=spotify.SourceAppUserModelId)
                    SetSession(spotify);
            }
            if(_session is null) {
                Clock.Reset();
                if(_hadSpotify){
                    _hadSpotify=false;_trackKey="";_cover=null;
                    Changed?.Invoke(new TrackState("CHARLI VISION","SPOTIFY STARTEN","",false,null,null));
                }
                return;
            }
            _hadSpotify=true;
            var info=_session.GetPlaybackInfo();
            bool playing=info?.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate=info?.PlaybackRate??1;
            if(rate<=0||!double.IsFinite(rate))rate=1;
            var timeline=_session.GetTimelineProperties();
            // Timestamp capture immediately follows the synchronous timeline read.
            long capture=Stopwatch.GetTimestamp();
            var observedUtc=DateTimeOffset.UtcNow;
            var length=timeline.EndTime-timeline.StartTime;
            bool valid=length>TimeSpan.FromSeconds(2)&&length<TimeSpan.FromHours(3)
                &&timeline.Position>=TimeSpan.Zero &&
                timeline.Position<=length+TimeSpan.FromSeconds(5)&&
                timeline.LastUpdatedTime.Year>=2020;
            var sample=new PlaybackClock(timeline.Position,length,timeline.LastUpdatedTime,
                playing,valid);
            Clock.Update(sample,rate,capture,observedUtc);

            if(_needsMetadata || (capture-_lastMetadata)/(double)Stopwatch.Frequency>4.0) {
                _needsMetadata=false;
                _lastMetadata=capture;
                var metaTimer=Stopwatch.StartNew();
                var meta=await _session.TryGetMediaPropertiesAsync();
                LastMetadataQueryMs=metaTimer.Elapsed.TotalMilliseconds;
                if(meta is null)return;
                string title=string.IsNullOrWhiteSpace(meta.Title)?"CHARLI VISION":meta.Title.Trim();
                string artist=string.IsNullOrWhiteSpace(meta.Artist)?"SPOTIFY":meta.Artist.Trim();
                string album=meta.AlbumTitle??"";
                string key=title+"\n"+artist+"\n"+album;
                bool changed=key!=_trackKey;
                if(changed){
                    _trackKey=key;
                    _cover=await ReadCover(meta.Thumbnail);
                    // Use latest captured timeline to update a reset clock for new media.
                    Clock.Update(sample,rate,capture,observedUtc,true);
                    LastRecognitionDelayMs=(Stopwatch.GetTimestamp()-began)*
                        1000d/Stopwatch.Frequency;
                }
                if(changed || playing!=_lastPlaying){
                    _lastPlaying=playing;
                    Changed?.Invoke(new TrackState(title,artist,album,playing,_cover,
                        Clock.Valid?Clock.Duration:null));
                }
            }else if(playing!=_lastPlaying){
                _lastPlaying=playing;
                // Keep metadata from the last successful read without blocking on an API call.
                var parts=_trackKey.Split('\n');
                Changed?.Invoke(new TrackState(parts.ElementAtOrDefault(0)??"CHARLI VISION",
                    parts.ElementAtOrDefault(1)??"SPOTIFY",parts.ElementAtOrDefault(2)??"",
                    playing,_cover,Clock.Valid?Clock.Duration:null));
            }
        } catch(Exception ex) {
            SettingsStore.Log(ex);
            try {_manager?.SessionsChanged-=ManagerChanged;}catch{}
            _manager=null;Unsubscribe();Clock.Reset();
            _retryAfter=Stopwatch.GetTimestamp()+3*Stopwatch.Frequency;
            _needsDiscovery=true;
        }finally {
            LastPollMs=(Stopwatch.GetTimestamp()-began)*1000d/Stopwatch.Frequency;
        }
    }
    private static async Task<BitmapSource?> ReadCover(IRandomAccessStreamReference? thumbnail)
    {
        if(thumbnail is null)return null;
        try {
            using var input=await thumbnail.OpenReadAsync();
            if(input.Size==0||input.Size>8_000_000)return null;
            using var reader=new DataReader(input);
            uint length=(uint)input.Size;
            await reader.LoadAsync(length);
            byte[] bytes=new byte[length];reader.ReadBytes(bytes);
            // Metadata and artwork can arrive during a track change. Decode
            // pixels off the Dispatcher, then freeze before returning to WPF.
            // This keeps expensive BitmapImage.EndInit off the vinyl frame path.
            return await Task.Run<BitmapSource>(() => {
                using var stream=new MemoryStream(bytes,false);
                var bmp=new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption=BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth=640;
                bmp.StreamSource=stream;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }).ConfigureAwait(false);
        }catch(Exception ex){SettingsStore.Log(ex);return null;}
    }
    public async Task Toggle(){if(_session is not null)await _session.TryTogglePlayPauseAsync();}
    public async Task Next(){if(_session is not null)await _session.TrySkipNextAsync();}
    public async Task Previous(){if(_session is not null)await _session.TrySkipPreviousAsync();}
    public bool ChangeSpotifyVolume(float delta,bool toggleMute=false)
    {
        try {
            using var enumerator=new MMDeviceEnumerator();
            using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
            var sessions=device.AudioSessionManager.Sessions;
            bool any=false;
            for(int i=0;i<sessions.Count;i++){
                var session=sessions[i];
                try {
                    using var proc=Process.GetProcessById((int)session.GetProcessID);
                    if(!proc.ProcessName.Contains("spotify",StringComparison.OrdinalIgnoreCase))continue;
                    if(toggleMute)session.SimpleAudioVolume.Mute=!session.SimpleAudioVolume.Mute;
                    else session.SimpleAudioVolume.Volume=
                        Math.Clamp(session.SimpleAudioVolume.Volume+delta,0,1);
                    any=true;
                }catch{}
            }
            return any;
        }catch(Exception ex){SettingsStore.Log(ex);return false;}
    }
    public void Dispose()
    {
        _disposed=true;
        try {_manager?.SessionsChanged-=ManagerChanged;}catch{}
        Unsubscribe();_manager=null;
    }
}