using System;

namespace CharliVision;

/// <summary>
/// One bounded network retry schedule per active track; uses cancellable
/// Task.Delay rather than any additional WPF rendering/animation timers.
/// </summary>
internal static class LyricsRecoveryPolicy
{
    public static TimeSpan Delay(int attempt,string? status)
    {
        bool transient=(status??"").Contains("nicht erreichbar",
            StringComparison.OrdinalIgnoreCase) ||
            (status??"").Contains("Rate",StringComparison.OrdinalIgnoreCase) ||
            (status??"").Contains("Provider",StringComparison.OrdinalIgnoreCase);
        int initialSeconds=transient?65:120;
        double seconds=initialSeconds*Math.Pow(2,
            Math.Clamp(attempt-1,0,3));
        return TimeSpan.FromSeconds(Math.Min(seconds,300));
    }
}
