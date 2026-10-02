using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>One new problem, as the summary notification needs it.</summary>
public sealed record AlertSummaryItem(AlertSeverity Severity, string Device, string Rule, string? Location);

/// <summary>
/// The text of the notification shown when several alerts arrive in one
/// poll: "3 new critical alerts", the worst one ("core-sw-02 — High
/// temperature"), then "+2 more", with "in Rack 3" when they all share a
/// location.
/// </summary>
public static class AlertSummaryText
{
    public static (string Title, string Body, string? Detail) Build(IReadOnlyList<AlertSummaryItem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        if (problems.Count == 0)
        {
            return ("Alert updates", "See DashyNMS for details.", null);
        }

        var severities = problems.Select(p => p.Severity).Distinct().ToList();
        var kind = severities.Count == 1 && severities[0] is AlertSeverity.Critical or AlertSeverity.Warning
            ? severities[0].ToDisplayString().ToLowerInvariant() + " "
            : string.Empty;

        var title = problems.Count == 1
            ? $"1 new {kind}alert"
            : $"{problems.Count} new {kind}alerts";

        // The worst first; among equals, the order they arrived in.
        var lead = problems
            .Select((p, index) => (p, index))
            .OrderByDescending(x => x.p.Severity)
            .ThenBy(x => x.index)
            .First().p;

        var body = string.IsNullOrWhiteSpace(lead.Rule) ? lead.Device : $"{lead.Device} — {lead.Rule}";

        if (problems.Count == 1)
        {
            return (title, body, null);
        }

        var locations = problems.Select(p => p.Location?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var where = locations.Count == 1 && !string.IsNullOrWhiteSpace(locations[0]) ? $" in {locations[0]}" : string.Empty;

        return (title, body, $"+{problems.Count - 1} more{where}");
    }
}
