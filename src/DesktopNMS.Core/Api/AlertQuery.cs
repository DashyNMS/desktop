using System.Globalization;
using System.Text;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Server-side filter for /api/v0/alerts.
/// </summary>
/// <remarks>
/// LibreNMS defaults to <c>state=1</c> (firing, unacknowledged) when no state is
/// given, and only supports a single severity value. DesktopNMS therefore asks
/// the server for the states it cares about and does severity filtering in the
/// UI, so toggling a filter chip does not cost a round trip.
/// </remarks>
public sealed class AlertQuery
{
    /// <summary>Alerts that are currently firing or acknowledged: the working set.</summary>
    public static AlertQuery Open { get; } = new()
    {
        States = new[] { AlertState.Active, AlertState.Acknowledged },
    };

    /// <summary>Firing, acknowledged and recovered.</summary>
    public static AlertQuery All { get; } = new()
    {
        States = new[] { AlertState.Recovered, AlertState.Active, AlertState.Acknowledged },
    };

    /// <summary>States to include. Null means "let the server default to state=1".</summary>
    public IReadOnlyCollection<AlertState>? States { get; init; }

    /// <summary>Optional server-side severity filter. The API accepts one value only.</summary>
    public AlertSeverity? Severity { get; init; }

    /// <summary>Restrict to a single alert rule.</summary>
    public int? RuleId { get; init; }

    /// <summary>Sort column and direction, e.g. "timestamp desc". Must be an alerts column.</summary>
    public string? OrderBy { get; init; } = "timestamp desc";

    /// <summary>Builds the relative URL, including the query string.</summary>
    public string ToRelativeUrl(string path = "alerts")
    {
        var builder = new StringBuilder(path);
        var first = true;

        void Append(string name, string value)
        {
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        if (States is { Count: > 0 })
        {
            Append("state", string.Join(",", States.Select(s => ((int)s).ToString(CultureInfo.InvariantCulture))));
        }

        var severity = Severity?.ToApiValue();
        if (severity is not null)
        {
            Append("severity", severity);
        }

        if (RuleId is > 0)
        {
            Append("alert_rule", RuleId.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(OrderBy))
        {
            Append("order", OrderBy!.Trim());
        }

        return builder.ToString();
    }

    public override string ToString() => ToRelativeUrl();
}
