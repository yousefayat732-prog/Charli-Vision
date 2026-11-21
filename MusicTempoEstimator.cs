using System;
using System.Collections.Generic;
using System.Linq;

namespace CharliVision;

/// <summary>
/// Conservative local bass-onset tempo estimator. Only emits BPM when there
/// is a repeatable pulse train in REAL incoming audio bands; otherwise null.
/// No track uploads, Spotify requests, fake BPM or background threads.
/// </summary>
public sealed class MusicTempoEstimator
{
    private readonly Queue<double> _onsets=new();
    private double _lastSample=double.NaN;
    private double _lastOnset=double.NegativeInfinity;
    private double _lastBass,_baseline;
    public double? EstimatedBpm {get;private set;}
    public double Confidence {get;private set;}
    public void Reset()
    {
        _onsets.Clear();
        _lastSample=double.NaN;
        _lastOnset=double.NegativeInfinity;
        _lastBass=0;
        _baseline=0;
        EstimatedBpm=null;
        Confidence=0;
    }
    public void Observe(double seconds,ReadOnlySpan<float> bands)
    {
        if(!double.IsFinite(seconds)||bands.Length<12)return;
        double bass=0;
        for(int i=0;i<12;i++){
            var sample=bands[i];
            if(float.IsFinite(sample))bass+=Math.Clamp(sample,0,1);
        }
        bass/=12;
        double dt=double.IsFinite(_lastSample)?
            Math.Clamp(seconds-_lastSample,0,.25):.016;
        _lastSample=seconds;
        _baseline+=(bass-_baseline)*(1-Math.Exp(-dt/.72));
        bool onset=bass>Math.Max(.12,_baseline*1.48) &&
            bass-_lastBass>Math.Max(.038,_baseline*.23) &&
            seconds-_lastOnset>.29;
        _lastBass=bass;
        if(onset){
            _lastOnset=seconds;
            _onsets.Enqueue(seconds);
            while(_onsets.Count>19)_onsets.Dequeue();
            Assess();
        }
        // Do not keep stale BPM when a track has gone silent for a long time.
        if(seconds-_lastOnset>5 && EstimatedBpm.HasValue){
            EstimatedBpm=null;
            Confidence=0;
        }
    }
    private void Assess()
    {
        if(_onsets.Count<9)return;
        var times=_onsets.ToArray();
        var intervals=new double[times.Length-1];
        for(int i=1;i<times.Length;i++){
            double interval=times[i]-times[i-1];
            if(interval<.31 || interval>1.2){
                EstimatedBpm=null;
                Confidence=0;
                return;
            }
            intervals[i-1]=interval;
        }
        Array.Sort(intervals);
        double median=(intervals[(intervals.Length-1)/2]+
            intervals[intervals.Length/2])*.5;
        double[] deviations=intervals.Select(x=>Math.Abs(x-median)).ToArray();
        Array.Sort(deviations);
        double deviation=(deviations[(deviations.Length-1)/2]+
            deviations[deviations.Length/2])*.5;
        double bpm=60/median;
        Confidence=Math.Clamp(1-deviation/Math.Max(.1,median),0,1);
        // A strong, stable pulse and plausible tempo are mandatory.
        EstimatedBpm=bpm is >=50 and <=185 && Confidence>=.86 &&
            intervals.Max()-intervals.Min()<median*.46?Math.Round(bpm,1):null;
    }
    public static double TargetRpm(double? bpm)=>
        bpm is double reliable && double.IsFinite(reliable)?
        Math.Clamp(33.333+(reliable-110)*.24,24,52):33.333;
}
