namespace DesktopNMS.Core.Intelligence;

/// <summary>A verified object mention that can be annotated inside prose, without adding another panel.</summary>
public sealed record IntelligenceInlineReference(string Key, string Name, string Status, string Tone, DateTimeOffset ObservedAt, string Detail);

public static class IntelligenceInlineReferences
{
    public static IEnumerable<IntelligenceInlineReference> FromSnapshot(IntelligenceStatusCard snapshot)
    {
        foreach (var row in snapshot.Rows)
        {
            if (row.DeviceId is { } id)
            {
                var state = string.IsNullOrEmpty(row.DeviceStatus) ? row.Status : row.DeviceStatus;
                // Alert severity is never a device's operational state.
                if (state is not ("Up" or "Down" or "Disabled" or "Unknown")) state = "Unknown";
                var tone = state == "Up" ? "up" : state == "Down" ? "down" : "unknown";
                yield return new("device:" + id, row.Name, state, tone, snapshot.ObservedAt, "Device status at lookup time");
            }
            if (row.AlertId is { } alertId && !string.IsNullOrWhiteSpace(row.AlertRule))
                yield return new("alert:" + alertId, AlertLabel(row.AlertRule, row.Status), row.Status, row.Tone, snapshot.ObservedAt, row.Name + " · " + row.Detail);
        }
    }

    private static string AlertLabel(string name, string severity)
    {
        // Keep the original rule in the tooltip, but don't repeat the badge's severity in its label.
        foreach (var separator in new[] { " - ", " – ", " — " })
            if (name.StartsWith(severity + separator, StringComparison.OrdinalIgnoreCase))
                return name[(severity.Length + separator.Length)..].Trim();
        return name;
    }
}
