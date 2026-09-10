using System;

namespace DesktopNMS.Services;

/// <summary>
/// Shared math for keeping a lazily-started poll loop (<see cref="SensorMonitor"/>,
/// <see cref="DeviceMonitor"/>) ticking in phase with <see cref="AlertMonitor"/>,
/// which is always running from the moment a session connects. Without this,
/// each loop's recurring interval is simply "time since I personally started",
/// so three tabs opened at three different moments would each refresh on
/// their own schedule - three different countdowns reaching zero at three
/// different times - even though they all use the same interval.
/// </summary>
public static class PollAlignment
{
    /// <summary>
    /// How long to wait before the next tick so it lands on the same
    /// wall-clock boundary <paramref name="anchor"/> would produce - i.e. the
    /// next multiple of <paramref name="intervalSeconds"/> since it started.
    /// Falls back to a plain fixed interval if there is no anchor yet.
    /// </summary>
    public static TimeSpan GetAlignedWait(DateTimeOffset? anchor, int intervalSeconds)
    {
        if (anchor is not { } startedAt || intervalSeconds <= 0)
        {
            return TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        }

        var elapsedSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
        var intoCurrentCycle = elapsedSeconds % intervalSeconds;
        var remaining = intervalSeconds - intoCurrentCycle;

        // A near-zero remainder (this tick landed almost exactly on a
        // boundary) would otherwise cause an immediate second poll.
        return remaining < 1
            ? TimeSpan.FromSeconds(intervalSeconds)
            : TimeSpan.FromSeconds(remaining);
    }

    /// <summary>
    /// Whole seconds until the next aligned tick, for a countdown display.
    /// Always recomputed fresh from wall-clock time rather than tracked as a
    /// running counter, so every consumer sharing the same anchor shows the
    /// exact same number at the exact same moment - see <see cref="Infrastructure.AutoRefreshTimer"/>.
    /// </summary>
    public static int GetSecondsRemaining(DateTimeOffset? anchor, int intervalSeconds)
        => (int)Math.Ceiling(GetAlignedWait(anchor, intervalSeconds).TotalSeconds);

    /// <summary>A short "45s" / "2:05" form for a status bar.</summary>
    public static string FormatRemaining(int secondsRemaining)
    {
        if (secondsRemaining <= 0)
        {
            return "due";
        }

        var span = TimeSpan.FromSeconds(secondsRemaining);
        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}:{span.Seconds:00}"
            : $"{secondsRemaining}s";
    }
}
