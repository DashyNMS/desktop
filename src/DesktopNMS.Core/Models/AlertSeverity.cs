namespace DesktopNMS.Core.Models;

/// <summary>
/// Severity of the alert rule that produced an alert. LibreNMS stores this on
/// the rule (alert_rules.severity), not on the alert itself.
/// </summary>
public enum AlertSeverity
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Critical = 3,
}

public static class AlertSeverityExtensions
{
    /// <summary>Maps the LibreNMS severity string ("ok", "warning", "critical").</summary>
    public static AlertSeverity Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "critical" => AlertSeverity.Critical,
        "crit" => AlertSeverity.Critical,
        "warning" => AlertSeverity.Warning,
        "warn" => AlertSeverity.Warning,
        "ok" => AlertSeverity.Ok,
        _ => AlertSeverity.Unknown,
    };

    /// <summary>The value to send back to the API in a severity query parameter.</summary>
    public static string? ToApiValue(this AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Ok => "ok",
        _ => null,
    };

    public static string ToDisplayString(this AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "Critical",
        AlertSeverity.Warning => "Warning",
        AlertSeverity.Ok => "OK",
        _ => "Unknown",
    };

    /// <summary>Higher is more urgent; used for sorting.</summary>
    public static int SortRank(this AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => 3,
        AlertSeverity.Warning => 2,
        AlertSeverity.Ok => 1,
        _ => 0,
    };
}
