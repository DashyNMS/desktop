using System.Globalization;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>One PoE power budget - a switch, or one unit/power supply of it - in watts.</summary>
public sealed record PoeBudgetRow(string? Label, double? UsedWatts, double? TotalWatts)
{
    public double? RemainingWatts => UsedWatts is { } used && TotalWatts is { } total ? Math.Max(0, total - used) : null;

    /// <summary>0-100, or null without both figures (or a zero total).</summary>
    public double? Percent => UsedWatts is { } used && TotalWatts is { } total && total > 0
        ? Math.Clamp(used / total * 100, 0, 100)
        : null;
}

/// <summary>A device's PoE budgets, and how many powered devices it reports, if it does.</summary>
public sealed record PoeSummary(IReadOnlyList<PoeBudgetRow> Budgets, int? DevicesConnected)
{
    public static PoeSummary None { get; } = new(Array.Empty<PoeBudgetRow>(), null);

    public bool HasAny => Budgets.Count > 0 || DevicesConnected is not null;
}

/// <summary>
/// A device's PoE budget (#54), read from the power sensors LibreNMS
/// discovers for it. LibreNMS has no per-port PoE data for most switches
/// (its per-port PoE graph comes back empty) - what it has is each PSE's
/// total, used and/or remaining power, named differently per platform:
/// <list type="bullet">
/// <item>ProCurve: "PoE Power Total" / "PoE Power Used".</item>
/// <item>ArubaOS-CX: "PoE Budget Total - ID n" / "PoE Budget Consumed - ID n".</item>
/// <item>IOS-XE: "PoE Budget Total - ID n", "PoE Budget Consumed/Remaining - Switch n - Power Supply A", and a "PoE Devices Connected" count.</item>
/// </list>
/// A budget's readings share the number at the end of their sensor index
/// ("pethMainPsePower.1", "cpeExtMainPseUsedPower.1"), which is what pairs
/// them - their descriptions don't always agree on how to name the unit.
/// </summary>
public static class PoeBudget
{
    private static readonly Regex PoeName = new(@"\b(PoE|PSE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PoeSummary FromSensors(IEnumerable<Sensor> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);

        var list = sensors.ToList();
        int? connected = null;

        var connectedSensor = list.FirstOrDefault(s =>
            string.Equals(s.SensorClass, "count", StringComparison.OrdinalIgnoreCase)
            && s.Description?.Contains("PoE Devices Connected", StringComparison.OrdinalIgnoreCase) == true);
        if (connectedSensor is not null)
        {
            connected = (int)Math.Round(connectedSensor.Current);
        }

        var groups = new Dictionary<string, (string? Label, double? Used, double? Total, double? Remaining)>(StringComparer.Ordinal);

        foreach (var sensor in list.Where(IsPoePower))
        {
            var kind = KindOf(sensor.Description!);
            if (kind is null)
            {
                continue;
            }

            var key = IndexSuffix(sensor.Index) ?? UnitOf(sensor.Description!) ?? string.Empty;
            groups.TryGetValue(key, out var group);

            // The most specific unit name any of its readings gives.
            var unit = UnitOf(sensor.Description!);
            if (unit is not null && (group.Label is null || unit.Length > group.Label.Length))
            {
                group.Label = unit;
            }

            switch (kind)
            {
                case Kind.Total: group.Total = sensor.Current; break;
                case Kind.Used: group.Used = sensor.Current; break;
                case Kind.Remaining: group.Remaining = sensor.Current; break;
            }

            groups[key] = group;
        }

        var rows = groups
            .OrderBy(kv => kv.Key, NaturalOrder.Instance)
            .Select(kv =>
            {
                var g = kv.Value;
                var total = g.Total ?? (g.Used is { } used && g.Remaining is { } remaining ? used + remaining : null);
                var usedWatts = g.Used ?? (total is { } t && g.Remaining is { } r ? Math.Max(0, t - r) : null);
                return new PoeBudgetRow(g.Label, usedWatts, total);
            })
            .Where(r => r.TotalWatts is not null || r.UsedWatts is not null)
            .ToList();

        // A single budget needs no unit name - it's the switch's.
        if (rows.Count == 1)
        {
            rows[0] = rows[0] with { Label = null };
        }

        return new PoeSummary(rows, connected);
    }

    private enum Kind
    {
        Total,
        Used,
        Remaining,
    }

    private static bool IsPoePower(Sensor s) =>
        string.Equals(s.SensorClass, "power", StringComparison.OrdinalIgnoreCase)
        && s.Description is { } d
        && PoeName.IsMatch(d);

    private static Kind? KindOf(string description)
    {
        var d = description.ToLowerInvariant();
        if (d.Contains("remaining", StringComparison.Ordinal) || d.Contains("available", StringComparison.Ordinal) && !d.Contains("total", StringComparison.Ordinal))
        {
            return Kind.Remaining;
        }

        if (d.Contains("used", StringComparison.Ordinal) || d.Contains("consumed", StringComparison.Ordinal) || d.Contains("consumption", StringComparison.Ordinal))
        {
            return Kind.Used;
        }

        if (d.Contains("total", StringComparison.Ordinal) || d.Contains("budget", StringComparison.Ordinal))
        {
            return Kind.Total;
        }

        return null;
    }

    /// <summary>"pethMainPsePower.1" -> "1" - what a budget's readings share.</summary>
    private static string? IndexSuffix(string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
        {
            return null;
        }

        var dot = index.LastIndexOf('.');
        var suffix = dot >= 0 ? index[(dot + 1)..] : index;
        return suffix.Length > 0 && suffix.All(char.IsDigit) ? suffix : null;
    }

    /// <summary>"PoE Budget Consumed - Switch 1 - Power Supply A" -> "Switch 1 - Power Supply A"; "... - ID 2" -> "Unit 2"; nothing after the dash -> null.</summary>
    private static string? UnitOf(string description)
    {
        var dash = description.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0)
        {
            return null;
        }

        var unit = description[(dash + 3)..].Trim();
        if (unit.Length == 0)
        {
            return null;
        }

        var id = Regex.Match(unit, @"^ID\s+(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return id.Success ? "Unit " + id.Groups[1].Value : unit;
    }

    /// <summary>Orders "2" before "10".</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static NaturalOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                && long.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                return a.CompareTo(b);
            }

            return string.CompareOrdinal(x, y);
        }
    }
}
