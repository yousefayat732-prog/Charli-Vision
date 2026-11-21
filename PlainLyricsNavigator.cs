using System;

namespace CharliVision;

/// <summary>
/// V5 estimated visual reading navigation for plain lyrics. It never creates
/// word/line timestamps; the position only moves the viewport smoothly.
/// </summary>
internal static class PlainLyricsNavigator
{
    internal static double Target(double scrollablePixels,TimeSpan? position,
        TimeSpan duration,double elapsedFallbackSeconds)
    {
        double height=Math.Max(0,scrollablePixels);
        if(height<.5)return 0;
        if(position is TimeSpan at && duration.TotalSeconds>20) {
            double length=duration.TotalSeconds;
            double lead=Math.Min(8,length*.04);
            double tail=Math.Min(10,length*.05);
            double ratio=(Math.Max(0,at.TotalSeconds)-lead)/
                Math.Max(1,length-lead-tail);
            return height*Math.Clamp(ratio,0,1);
        }
        // No verified clock: deterministic calm reading pace, not fake sync.
        return Math.Clamp(Math.Max(0,elapsedFallbackSeconds)*12,0,height);
    }
    internal static double Smooth(double current,double target,double max,
        double elapsedSeconds)
    {
        double extent=Math.Max(0,max);
        target=Math.Clamp(target,0,extent);
        double delta=target-current;
        // Seeking far ahead/back must never leave lyrics lagging many seconds.
        if(Math.Abs(delta)>Math.Max(110,extent*.28))return target;
        double step=Math.Clamp(elapsedSeconds,0,.12);
        double blend=1-Math.Exp(-step/.24);
        return Math.Clamp(current+delta*blend,0,extent);
    }
}
