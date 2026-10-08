using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>How often a rule has fired on a device before, within a period (#270).</summary>
/// <param name="Count">Times it went into alert before this one.</param>
/// <param name="LastFired">The latest of those, in local time - null when there were none.</param>
/// <param name="Period">The period counted over.</param>
public sealed record AlertHistorySummary(int Count, DateTime? LastFired, TimeSpan Period)
{
    /// <summary>"Fired 6 times in the last 30 days · last 2 days ago", or "No earlier alerts in the last 30 days".</summary>
    public string Describe(DateTime localNow)
    {
        var days = (int)Math.Round(Period.TotalDays);
        var span = $"in the last {days} day{(days == 1 ? string.Empty : "s")}";
        if (Count == 0 || LastFired is not { } last)
        {
            return $"No earlier alerts {span}";
        }

        var times = Count == 1 ? "once" : $"{Count} times";
        return $"Fired {times} {span} · last {Ago(localNow - last)}";
    }

    private static string Ago(TimeSpan ago) => ago switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)ago.TotalMinutes}m ago",
        { TotalDays: < 1 } => $"{(int)ago.TotalHours}h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)ago.TotalDays} days ago",
    };
}

/// <summary>
/// Counts a rule's earlier firings on a device from LibreNMS's alert log
/// (#270) - shared with DashyNMS Mobile, whose alert page shows the same, to
/// tell a one-off from a flapping alert. Only the times it went into alert
/// count (state 1): recoveries, acknowledgements and worse/better updates are
/// rows in the same log, but not firings.
/// </summary>
public static class AlertHistory
{
    /// <summary>The period counted over by default.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);

    /// <param name="entries">The device's alert log, any order.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="deviceId">The device.</param>
    /// <param name="localNow">Now, in local time.</param>
    /// <param name="serverTimestampsAreUtc">How the server stores its times - see <see cref="Configuration.AppSettings.ServerTimestampsAreUtc"/>.</param>
    /// <param name="excludeCurrent">Leave out the newest firing, as it's the alert being looked at.</param>
    /// <param name="period">How far back to count; <see cref="DefaultPeriod"/> if null.</param>
    public static AlertHistorySummary Summarise(
        IEnumerable<AlertLogEntry> entries,
        int ruleId,
        int deviceId,
        DateTime localNow,
        bool serverTimestampsAreUtc,
        bool excludeCurrent = true,
        TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var within = period ?? DefaultPeriod;
        var since = localNow - within;

        var firings = entries
            .Where(e => e.RuleId == ruleId && e.DeviceId == deviceId && e.State == AlertState.Active && e.TimeLogged is not null)
            .OrderByDescending(e => e.Id)
            .Select(e => ServerTime.ToLocal(e.TimeLogged!.Value, serverTimestampsAreUtc))
            .ToList();

        if (excludeCurrent && firings.Count > 0)
        {
            firings.RemoveAt(0);
        }

        var counted = firings.Where(t => t >= since).ToList();
        return new AlertHistorySummary(counted.Count, counted.Count > 0 ? counted.Max() : null, within);
    }
}
