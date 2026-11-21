using System;

namespace CharliVision;

/// <summary>
/// Vsync-driven frame budget. The next presentation deadline stays anchored
/// to monotonic time, not the previous *late* compositor callback.
/// This avoids adding a whole extra target interval after delayed callbacks.
/// </summary>
public sealed class FramePacingScheduler
{
    private double _nextDue,_budgetMs;
    public void Reset(){_nextDue=0;_budgetMs=0;}
    public bool ShouldUpdate(double monotonicSeconds,double requestedFrameMs)
    {
        if(!double.IsFinite(monotonicSeconds)||
            !double.IsFinite(requestedFrameMs)||requestedFrameMs<=0)return false;
        double budget=Math.Clamp(requestedFrameMs,5,1000);
        double step=budget/1000;
        if(_nextDue<=0||Math.Abs(_budgetMs-budget)>.1){
            _nextDue=monotonicSeconds;
            _budgetMs=budget;
        }
        // A compositor callback can arrive slightly before the ideal deadline.
        // A rigid 0.5ms threshold skips an otherwise usable vsync and can
        // create a visible ~33ms gap at a 60Hz target. Still cap fast callbacks.
        double earlyTolerance=Math.Min(.002,step*.12);
        if(monotonicSeconds+earlyTolerance<_nextDue)return false;
        _nextDue+=step;
        // Missed composition callback? Resync without carrying all missed
        // deadlines or forcing another entire interval of dead time.
        if(_nextDue<monotonicSeconds)
            _nextDue=monotonicSeconds+Math.Max(.005,step*.25);
        return true;
    }
}
