using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>How many alerts a count takes in, and whether any of them is critical.</summary>
public readonly record struct AlertCount(int Total, int Critical, int Warning)
{
    public bool IsCritical => Critical > 0;
}

/// <summary>
/// What the tray icon and the Alerts badge count (#269) - one method, so the
/// two always agree, and so DashyNMS Mobile's app icon and Alerts tab dot can
/// count the same way. The Alerts page itself still lists everything.
/// </summary>
public static class AlertCounting
{
    /// <summary>The choices, most inclusive first, as Settings lists them.</summary>
    public static IReadOnlyList<AlertSeverity> Choices { get; } = new[] { AlertSeverity.Ok, AlertSeverity.Warning, AlertSeverity.Critical };

    /// <summary>"Critical only", "Critical and warning", "Every alert".</summary>
    public static string Describe(AlertSeverity minimum) => minimum switch
    {
        AlertSeverity.Critical => "Critical only",
        AlertSeverity.Warning => "Critical and warning",
        _ => "Every alert",
    };

    /// <summary>
    /// Whether <paramref name="alert"/> counts: active, or acknowledged when
    /// those count, and at least as serious as <paramref name="minimum"/>. At
    /// <see cref="AlertSeverity.Ok"/> every severity counts, an alert of no
    /// known severity included - how the count always worked.
    /// </summary>
    public static bool Counts(Alert alert, AlertSeverity minimum, bool includeAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return Counts(alert.State, alert.Severity, minimum, includeAcknowledged);
    }

    /// <inheritdoc cref="Counts(Alert, AlertSeverity, bool)"/>
    public static bool Counts(AlertState state, AlertSeverity severity, AlertSeverity minimum, bool includeAcknowledged)
    {
        var open = state == AlertState.Active || (state == AlertState.Acknowledged && includeAcknowledged);
        return open && (minimum.SortRank() <= AlertSeverity.Ok.SortRank() || severity.SortRank() >= minimum.SortRank());
    }

    /// <summary>Counts <paramref name="alerts"/> - see <see cref="Counts(Alert, AlertSeverity, bool)"/>.</summary>
    public static AlertCount Count(IEnumerable<Alert> alerts, AlertSeverity minimum, bool includeAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        int total = 0, critical = 0, warning = 0;
        foreach (var alert in alerts)
        {
            if (!Counts(alert, minimum, includeAcknowledged))
            {
                continue;
            }

            total++;
            if (alert.Severity == AlertSeverity.Critical)
            {
                critical++;
            }
            else if (alert.Severity == AlertSeverity.Warning)
            {
                warning++;
            }
        }

        return new AlertCount(total, critical, warning);
    }
}
