using System;
using System.Diagnostics;

namespace CharliVision;

/// <summary>
/// One monotonic playback clock. DateTimeOffset is used ONLY to translate a
/// Windows timestamp to the instant of a sample, never as a render timer.
/// </summary>
public sealed class PrecisionPlaybackClock
{
    private long _anchorTicks;
    private double _anchorSeconds;
    private double _durationSeconds;
    private double _rate=1;
    private double _correctionSeconds,_correctionWindow;
    private bool _playing,_valid;
    private double _lastErrorMs,_sampleAgeMs;
    private DateTimeOffset _lastUpdated;
    public bool Valid=>_valid;
    public bool Playing=>_playing;
    public TimeSpan Duration=>TimeSpan.FromSeconds(_durationSeconds);
    public double Rate=>_rate;
    public double LastErrorMs=>_lastErrorMs;
    public double SampleAgeMs=>_sampleAgeMs;
    internal long PositionRevision { get; private set; }
    public DateTimeOffset LastUpdatedTime=>_lastUpdated;
    public static long Stamp()=>Stopwatch.GetTimestamp();

    public void Reset()
    {
        if (_valid) PositionRevision++;
        _valid=false;_playing=false;_anchorTicks=0;_anchorSeconds=0;
        _durationSeconds=0;_lastErrorMs=0;_sampleAgeMs=0;
        _correctionSeconds=0;_correctionWindow=0;
        _lastUpdated=DateTimeOffset.MinValue;
    }
    public void Update(PlaybackClock raw,double playbackRate,
        long observationTicks,DateTimeOffset observationUtc,bool newTrack=false)
    {
        if(!raw.Valid || raw.Duration<=TimeSpan.Zero ||
            double.IsNaN(playbackRate) || double.IsInfinity(playbackRate)) {
            Reset();return;
        }
        var playing=raw.Playing;
        double rate=Math.Clamp(playbackRate,.1,4);
        double seconds=raw.Position.TotalSeconds;
        double age=(observationUtc-raw.LastUpdated).TotalSeconds;
        _sampleAgeMs=age*1000;
        _lastUpdated=raw.LastUpdated;
        // Windows defines Position at LastUpdatedTime. Translate at capture once.
        // Reject clearly future/implausibly old timestamps rather than extrapolate a
        // stale value for minutes and falsely claim precise lyrics.
        bool trustedAge=age>=-1 && age<=120;
        if(playing && trustedAge) seconds+=Math.Max(0,age)*rate;
        if(playing && !trustedAge && _valid && !newTrack && _playing){
            // Maintain the last reliable monotonic anchor if Windows stopped
            // refreshing timestamps; do not blindly reset the song to old Position.
            seconds=EstimateSeconds(observationTicks);
        }
        seconds=Math.Clamp(seconds,0,raw.Duration.TotalSeconds);
        double prediction=_valid?EstimateSeconds(observationTicks):seconds;
        double delta=seconds-prediction;
        _lastErrorMs=delta*1000;

        // A new track, seek, stop or pause is not a small clock drift.
        bool jumped=newTrack || !_valid || playing!=_playing ||
            Math.Abs(_durationSeconds-raw.Duration.TotalSeconds)>2 ||
            Math.Abs(delta)>.34;
        if (newTrack || !_valid || Math.Abs(_durationSeconds-raw.Duration.TotalSeconds)>2 ||
            (playing==_playing && Math.Abs(delta)>.34))
            PositionRevision++;
        _correctionSeconds=0;
        _correctionWindow=0;
        if (!jumped && playing)
        {
            // Correct ordinary drift continuously at up to 40% of playback
            // rate. A negative adjustment can slow but cannot reverse time.
            _correctionSeconds=delta;
            _correctionWindow=Math.Max(.18,Math.Abs(delta)/(.40*rate));
            seconds=prediction;
        }
        _anchorTicks=observationTicks;
        _anchorSeconds=seconds;
        _durationSeconds=raw.Duration.TotalSeconds;
        _rate=rate;
        _playing=playing;
        _valid=true;
    }
    private double EstimateSeconds(long ticks)
    {
        if(!_valid)return 0;
        double elapsed=Math.Max(0,(ticks-_anchorTicks)/(double)Stopwatch.Frequency);
        double correction=_correctionWindow>0?
            _correctionSeconds*Math.Min(1,elapsed/_correctionWindow):0;
        double value=_anchorSeconds+(_playing ? elapsed*_rate+correction : 0);
        return Math.Clamp(value,0,_durationSeconds);
    }
    public TimeSpan? Estimated() => Estimated(Stamp());
    public TimeSpan? Estimated(long ticks) => _valid?
        TimeSpan.FromSeconds(EstimateSeconds(ticks)):null;
}
