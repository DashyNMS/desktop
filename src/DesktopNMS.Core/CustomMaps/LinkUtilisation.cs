using System.Globalization;

namespace DesktopNMS.Core.CustomMaps;

/// <summary>
/// How a custom map colours, sizes and labels a link from its port's
/// traffic - the same rules as LibreNMS's CustomMapDataController and
/// custom-js (legendPctDefaultColour, fixedColour, speedWidth, rateString),
/// so a map looks the same in both.
/// </summary>
public static class LinkUtilisation
{
    /// <summary>LibreNMS's colour for a link it can't measure (no speed, no port data).</summary>
    public const string UnknownColour = "#000000";

    /// <summary>LibreNMS's colour for a link whose device or port is down.</summary>
    public const string DownColour = "#8B0000";

    /// <summary>Rate as a percentage of speed; -1 when the speed is unknown (LibreNMS's "invalid").</summary>
    public static double Percent(double? ratebps, long? speedbps) =>
        speedbps is > 0 && ratebps is { } rate ? rate / speedbps.Value * 100.0 : -1;

    /// <summary>
    /// The colour for a percentage: the map's fixed steps if it has any
    /// (the highest step at or below the value), otherwise LibreNMS's
    /// gradient - green at 0%, yellow at 50%, red at 100%, purple from 150%.
    /// </summary>
    public static string Colour(double percent, IReadOnlyDictionary<string, string>? fixedColours)
    {
        if (percent < 0)
        {
            return fixedColours?.GetValueOrDefault("-1") ?? UnknownColour;
        }

        if (fixedColours is { Count: > 0 })
        {
            var colour = UnknownColour;
            foreach (var (step, stepColour) in fixedColours
                .Select(kv => (Step: double.TryParse(kv.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : double.NaN, kv.Value))
                .Where(s => s.Step >= 0)
                .OrderBy(s => s.Step))
            {
                if (step > percent)
                {
                    break;
                }

                colour = stepColour;
            }

            return colour;
        }

        return GradientColour(percent);
    }

    /// <summary>LibreNMS's legendPctDefaultColour.</summary>
    public static string GradientColour(double percent)
    {
        static string Hex(double value) => ((int)Math.Clamp(value, 0, 255)).ToString("x2", CultureInfo.InvariantCulture);

        if (percent < 0)
        {
            return UnknownColour;
        }

        if (percent < 50)
        {
            // Green, adding red on the way to yellow.
            return "#" + Hex(5.1 * percent) + "ff00";
        }

        if (percent < 100)
        {
            // Red, removing green on the way from yellow to red.
            return "#ff" + Hex(5.1 * (100.0 - percent)) + "00";
        }

        if (percent < 150)
        {
            // Red, adding blue on the way to purple.
            return "#ff00" + Hex(5.1 * (percent - 100.0));
        }

        return "#ff00ff";
    }

    /// <summary>LibreNMS's speedWidth: 1 below 1 Mbps, then half a unit per extra digit - 1G ≈ 2.5, 10G ≈ 3, 100G ≈ 3.5.</summary>
    public static double Width(long? speedbps) =>
        speedbps is not { } speed || speed < 1_000_000 ? 1.0 : (speed.ToString(CultureInfo.InvariantCulture).Length - 5) / 2.0;

    /// <summary>"1.25 Gbps" - LibreNMS's rateString.</summary>
    public static string Rate(double? bps)
    {
        if (bps is not { } value || value < 0)
        {
            return string.Empty;
        }

        string[] units = { "bps", "kbps", "Mbps", "Gbps", "Tbps" };
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>
    /// The legend's rows, top to bottom: fixed steps as defined, or the
    /// gradient sampled at evenly spaced percentages up to 150% (100% with
    /// overspeed hidden), as LibreNMS's redrawDefaultLegend does.
    /// </summary>
    public static IReadOnlyList<(string Label, string Colour)> LegendRows(CustomMapLegend legend)
    {
        var rows = new List<(string, string)>();

        if (!legend.HideInvalid)
        {
            rows.Add(("Unknown", legend.Colours?.GetValueOrDefault("-1") ?? UnknownColour));
        }

        if (legend.Colours is { Count: > 0 } colours)
        {
            foreach (var (step, colour) in colours
                .Select(kv => (Step: double.TryParse(kv.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : double.NaN, kv.Value))
                .Where(s => s.Step >= 0)
                .OrderBy(s => s.Step))
            {
                rows.Add(($"{step,3:0}%", colour));
            }

            return rows;
        }

        var steps = Math.Max(2, legend.Steps);
        var top = legend.HideOverspeed ? 100.0 : 150.0;
        for (var i = 0; i < steps; i++)
        {
            var percent = Math.Round(top / (steps - 1) * i);
            rows.Add(($"{percent,3:0}%", GradientColour(percent)));
        }

        return rows;
    }
}
