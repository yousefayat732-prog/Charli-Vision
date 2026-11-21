using System;
using System.Diagnostics;

namespace CharliVision;

/// <summary>
/// Lightweight presented-update timing, NOT a GPU/compositor FPS counter.
/// One fixed-size ring buffer; no per-frame heap allocations.
/// </summary>
public sealed class FramePacingStats
{
    private const int Capacity=1024;
    private readonly double[] _frameMs=new double[Capacity];
    private int _count,_next;
    private long _lastStamp;
    private long _estimatedMisses;
    public int Count=>_count;
    public long EstimatedMisses=>_estimatedMisses;
    public void Reset()
    {
        _count=0;_next=0;_lastStamp=0;_estimatedMisses=0;
    }
    public void AddFrame(long timestamp,double targetMs)
    {
        if(_lastStamp!=0 && timestamp>_lastStamp){
            double interval=(timestamp-_lastStamp)*1000d/Stopwatch.Frequency;
            if(double.IsFinite(interval) && interval>0 && interval<=5_000){
                _frameMs[_next]=interval;
                _next=(_next+1)%Capacity;
                if(_count<Capacity)_count++;
                if(targetMs>0 && interval>targetMs*1.6)
                    _estimatedMisses+=Math.Max(0,(long)Math.Round(interval/targetMs)-1);
            }
        }
        _lastStamp=timestamp;
    }
    public FramePacingSample GetSample()
    {
        if(_count==0)return new FramePacingSample(0,0,0,0,0,0,_estimatedMisses);
        var data=new double[_count];
        Array.Copy(_frameMs,data,_count);
        Array.Sort(data);
        double sum=0;
        foreach(double ms in data)sum+=ms;
        int Percentile(double fraction)=>Math.Clamp(
            (int)Math.Ceiling(fraction*_count)-1,0,_count-1);
        return new FramePacingSample(_count,sum/_count,
            data[Percentile(.50)],data[Percentile(.95)],
            data[Percentile(.99)],data[^1],_estimatedMisses);
    }
}
public readonly record struct FramePacingSample(
    int Samples,double MeanMs,double MedianMs,double P95Ms,double P99Ms,
    double MaxMs,long EstimatedMisses);
