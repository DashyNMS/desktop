using System.Globalization;
using System.Text;

namespace DesktopNMS.Demo;

/// <summary>
/// Graphs for demo mode, drawn the way LibreNMS's rrdtool SVGs are - the same
/// elements, attribute colours and LibreNMS's own light palette and series
/// colours - so they go through <c>GraphSvgStyle</c> just as a real server's
/// do. A smooth day of made-up but plausible data.
/// </summary>
internal static class DemoGraphs
{
    private const string Black = "rgb(0%, 0%, 0%)";
    private const string MinorGrid = "rgb(64.705882%, 64.705882%, 64.705882%)";
    private const string MajorGrid = "rgb(100%, 60%, 60%)";
    private const string Axis = "rgb(12%, 12%, 12%)";
    private const int Left = 52;
    private const int Top = 10;
    private const int Bottom = 34;

    // LibreNMS's own: port traffic (generic_bits), ping (icmp_perf), and the "mixed" palette's first colour.
    /// <param name="now">The device's traffic now, in and out, in Gb/s - so the graph ends where the legend says; null for a port graph.</param>
    public static string Traffic(int width, int height, int seed, double load, (double In, double Out)? now = null)
    {
        var max = Math.Max(1, load) * 10;
        double inLevel = 0.25 + load * 0.6, outLevel = inLevel * 0.45;
        if (now is { In: > 0 } rates)
        {
            // The day's last samples sit at about 0.6 of a series' level, its peak at about 1.15.
            max = Math.Ceiling(rates.In * 2.5 / 5) * 5;
            inLevel = rates.In / max / 0.6;
            outLevel = rates.Out / max / 0.6;
        }

        var inbound = Series(seed, inLevel, 0.18);
        var outbound = Series(seed + 7, outLevel, 0.12);
        return Chart(width, height, "bps", max, new[]
        {
            new Line(inbound, "#91B13C", "#006600", "In"),
            new Line(outbound, "#8080BD", "#000099", "Out"),
        });
    }

    public static string Percent(int width, int height, int seed, double level)
        => Chart(width, height, "%", 100, new[] { new Line(Series(seed, Math.Clamp(level, 0.05, 0.95), 0.08), "#F5CCCC", "#CC0000", "Used") });

    public static string Latency(int width, int height, int seed)
        => Chart(width, height, "ms", 10, new[] { new Line(Series(seed, 0.22, 0.1), "#CCD2DE", "#36393D", "RTT") });

    /// <summary>A sensor class's graph: a line per sensor around its current value, in LibreNMS's sensor colours (sensor.inc.php).</summary>
    public static string Sensors(int width, int height, int seed, IReadOnlyList<(string Name, double Current)> sensors)
    {
        string[] colours = ["#CC0000", "#008C00", "#4096EE", "#73880A", "#D01F3C", "#36393D", "#FF0084"];
        var max = Math.Max(1, sensors.Count == 0 ? 1 : sensors.Max(s => Math.Abs(s.Current))) * 1.4;
        var lines = sensors.Select((s, i) =>
        {
            var values = Series(seed + i * 11, Math.Clamp(Math.Abs(s.Current) / max, 0.05, 0.9), 0.04);
            return new Line(values, null, colours[i % colours.Length], s.Name);
        }).ToList();
        return Chart(width, height, string.Empty, Math.Round(max), lines);
    }

    /// <summary>The graph without its legend, as LibreNMS draws it for "legend=no".</summary>
    public static string WithoutLegend(string svg)
        => System.Text.RegularExpressions.Regex.Replace(svg, "<(path|text) class=\"legend\"[^>]*?(/>|>[^<]*</text>)", string.Empty);

    private sealed record Line(double[] Values, string? Area, string Stroke, string Label);

    /// <summary>288 five-minute samples, 0..1, with a working-day hump.</summary>
    private static double[] Series(int seed, double level, double noise)
    {
        var random = new Random(seed);
        var values = new double[288];
        var drift = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            var hour = i / 12.0;
            var day = Math.Exp(-Math.Pow((hour - 14) / 4.5, 2));
            drift = drift * 0.96 + (random.NextDouble() - 0.5) * noise * 0.35;
            values[i] = Math.Clamp(level * (0.55 + 0.6 * day) + drift, 0.02, 0.98);
        }

        return values;
    }

    private static string Chart(int width, int height, string unit, double max, IReadOnlyList<Line> series)
    {
        width = Math.Max(width, 200);
        height = Math.Max(height, 100);
        var plotW = width - Left - 12;
        var plotH = height - Top - Bottom;
        var bottom = Top + plotH;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">\n");

        // rrdtool's grid: minor lines between major ones, and major time divisions.
        for (var g = 0; g <= 8; g++)
        {
            var y = bottom - plotH * g / 8.0;
            var colour = g % 2 == 0 ? MajorGrid : MinorGrid;
            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" stroke-width=\"0.6\" stroke=\"{colour}\" stroke-opacity=\"1\" stroke-dasharray=\"1 1\" d=\"M {Left} {y:0.##} L {Left + plotW} {y:0.##} \"/>\n");
        }

        foreach (var hour in new[] { 6, 12, 18 })
        {
            var x = Left + plotW * hour / 24.0;
            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" stroke-width=\"0.6\" stroke=\"{MajorGrid}\" stroke-opacity=\"1\" stroke-dasharray=\"1 1\" d=\"M {x:0.##} {bottom} L {x:0.##} {Top} \"/>\n");
        }

        svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" stroke-width=\"0.6\" stroke=\"{Axis}\" stroke-opacity=\"1\" d=\"M {Left} {bottom} L {Left + plotW} {bottom} \"/>\n");

        for (var g = 0; g <= 4; g++)
        {
            var y = bottom - plotH * g / 4.0;
            var label = unit == "bps" ? $"{max * g / 4:0.#} G" : $"{max * g / 4:0.#}";
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{Left - 6}\" y=\"{y + 3:0.#}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" text-anchor=\"end\" fill=\"{Black}\">{label}</text>\n");
        }

        foreach (var hour in new[] { 0, 6, 12, 18, 24 })
        {
            var x = Left + plotW * hour / 24.0;
            var text = DateTime.Now.AddHours(hour - 24).ToString("HH:00", CultureInfo.InvariantCulture);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:0.#}\" y=\"{bottom + 13}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" text-anchor=\"middle\" fill=\"{Black}\">{text}</text>\n");
        }

        var legendX = Left;
        foreach (var line in series)
        {
            var points = new StringBuilder();
            for (var i = 0; i < line.Values.Length; i++)
            {
                var x = Left + plotW * i / (double)(line.Values.Length - 1);
                var y = bottom - plotH * line.Values[i];
                points.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "M" : "L")} {x:0.##} {y:0.##} ");
            }

            if (line.Area is not null)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<path fill-rule=\"nonzero\" fill=\"{Rgb(line.Area)}\" fill-opacity=\"1\" d=\"M {Left} {bottom} L{points.ToString()[1..]}L {Left + plotW} {bottom} Z\"/>\n");
            }

            svg.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" stroke-width=\"1.25\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"{Rgb(line.Stroke)}\" stroke-opacity=\"1\" d=\"{points}\"/>\n");
            svg.Append(CultureInfo.InvariantCulture, $"<path class=\"legend\" fill-rule=\"nonzero\" fill=\"{Rgb(line.Stroke)}\" fill-opacity=\"1\" d=\"M {legendX} {height - 11} L {legendX} {height - 3} L {legendX + 8} {height - 3} L {legendX + 8} {height - 11} Z\"/>\n");
            svg.Append(CultureInfo.InvariantCulture, $"<text class=\"legend\" x=\"{legendX + 12}\" y=\"{height - 4}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" fill=\"{Black}\">{line.Label}</text>\n");
            legendX += 60;
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    /// <summary>"#91B13C" as rrdtool writes it: "rgb(56.862745%, 69.411765%, 23.529412%)".</summary>
    private static string Rgb(string hex)
    {
        string Channel(int at) => (int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 100d / 255).ToString("0.######", CultureInfo.InvariantCulture) + "%";
        return $"rgb({Channel(1)}, {Channel(3)}, {Channel(5)})";
    }
}
