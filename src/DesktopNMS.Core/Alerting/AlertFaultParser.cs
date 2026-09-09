using System.Globalization;
using System.Text.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Turns the <c>details</c> blob on an alert_log row into printable faults.
/// </summary>
/// <remarks>
/// The shape is <c>{ "contacts": {...}, "rule": [ row, ... ], "diff": { "added": [...], "resolved": [...] } }</c>,
/// where each row is the raw result of the alert rule's SQL query. Those
/// queries join <c>devices</c> and select everything, so a row is mostly the
/// device's own record: credentials are dropped, configuration is dropped, and
/// the columns the rule's condition names are promoted to the top.
/// </remarks>
public static class AlertFaultParser
{
    public static AlertDetail Parse(AlertLogEntry? entry, IReadOnlySet<string>? ruleFields = null)
    {
        if (entry?.Details is not { ValueKind: JsonValueKind.Object } details)
        {
            return AlertDetail.Empty;
        }

        var fields = ruleFields ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var faults = ParseRows(details, "rule", fields);

        IReadOnlyList<AlertFault> added = Array.Empty<AlertFault>();
        IReadOnlyList<AlertFault> resolved = Array.Empty<AlertFault>();

        if (details.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.Object)
        {
            added = ParseRows(diff, "added", fields);
            resolved = ParseRows(diff, "resolved", fields);
        }

        return new AlertDetail(faults, added, resolved, entry.TimeLogged, fields.Count > 0);
    }

    private static IReadOnlyList<AlertFault> ParseRows(
        JsonElement parent,
        string propertyName,
        IReadOnlySet<string> ruleFields)
    {
        if (!parent.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AlertFault>();
        }

        var faults = new List<AlertFault>();

        foreach (var row in array.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fault = ParseRow(row, ruleFields);
            if (fault is not null)
            {
                faults.Add(fault);
            }
        }

        return faults;
    }

    private static AlertFault? ParseRow(JsonElement row, IReadOnlySet<string> ruleFields)
    {
        var ordering = new List<(int Rank, int Order, AlertFaultField Field)>();
        var order = 0;

        foreach (var property in row.EnumerateObject())
        {
            var name = property.Name;

            // Non-negotiable: credentials never reach the UI.
            if (LibreNmsSchema.IsSecret(name))
            {
                continue;
            }

            var value = Stringify(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var isTrigger = ruleFields.Contains(name);

            // Device configuration is noise, unless the rule specifically tests it.
            if (!isTrigger && LibreNmsSchema.IsDevicePlumbing(name))
            {
                continue;
            }

            var kind = Classify(name);

            // Trigger columns first, then identity, description, metric, device context.
            var rank = isTrigger && kind is AlertFaultFieldKind.Metric or AlertFaultFieldKind.DeviceContext
                ? -1
                : (int)kind;

            ordering.Add((rank, order++, new AlertFaultField(name, value!, kind, isTrigger)));
        }

        if (ordering.Count == 0)
        {
            return null;
        }

        var fields = ordering
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Order)
            .Select(x => x.Field)
            .ToArray();

        return new AlertFault(fields);
    }

    private static AlertFaultFieldKind Classify(string name)
    {
        if (LibreNmsSchema.IsIdentity(name))
        {
            return AlertFaultFieldKind.Identity;
        }

        if (LibreNmsSchema.IsDescription(name))
        {
            return AlertFaultFieldKind.Description;
        }

        // Columns from the devices table rank below the table the rule targets,
        // which is where the metric that fired almost always lives.
        return LibreNmsSchema.IsDeviceOperational(name)
            ? AlertFaultFieldKind.DeviceContext
            : AlertFaultFieldKind.Metric;
    }

    private static string? Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => FormatNumber(value),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    private static string FormatNumber(JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
        {
            return integer.ToString("N0", CultureInfo.CurrentCulture);
        }

        if (value.TryGetDouble(out var number))
        {
            // Rule queries routinely return percentages with a long tail of
            // floating point noise; two decimals is plenty for a human.
            return Math.Round(number, 2).ToString("N2", CultureInfo.CurrentCulture);
        }

        return value.GetRawText();
    }
}
