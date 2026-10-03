using System.Globalization;
using System.Text;

namespace DesktopNMS.Demo;

/// <summary>
/// Graphs for demo mode, drawn the way LibreNMS's rrdtool SVGs are:
/// transparent, with black text (which <c>GraphSvgTheming</c> recolours for
/// the theme) - a smooth day of made-up but plausible traffic.
/// </summary>
internal static class DemoGraphs
{
    private const string Text = "fill=\"rgb(0%, 0%, 0%)\"";
    private const int Left = 52;
    private const int Top = 10;
    private const int Bottom = 34;

    public static string Traffic(int width, int height, int seed, double load)
    {
        var inbound = Series(seed, 0.25 + load * 0.6, 0.18);
        var outbound = Series(seed + 7, (0.25 + load * 0.6) * 0.45, 0.12);
        return Chart(width, height, "bps", Math.Max(1, load) * 10, new[]
        {
            (inbound, "#3B82F6", true, "In"),
            (outbound, "#2EA043", false, "Out"),
        });
    }

    public static string Percent(int width, int height, int seed, double level)
        => Chart(width, height, "%", 100, new[] { (Series(seed, Math.Clamp(level, 0.05, 0.95), 0.08), "#3B82F6", true, "Used") });

    public static string Latency(int width, int height, int seed)
        => Chart(width, height, "ms", 10, new[] { (Series(seed, 0.22, 0.1), "#DB9A04", false, "Ping") });

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

    private static string Chart(int width, int height, string unit, double max, IReadOnlyList<(double[] Values, string Colour, bool Area, string Label)> series)
    {
        width = Math.Max(width, 200);
        height = Math.Max(height, 100);
        var plotW = width - Left - 12;
        var plotH = height - Top - Bottom;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

        for (var g = 0; g <= 4; g++)
        {
            var y = Top + plotH - plotH * g / 4.0;
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{y:0.#}\" x2=\"{Left + plotW}\" y2=\"{y:0.#}\" stroke=\"#808080\" stroke-opacity=\"0.25\" stroke-dasharray=\"2,3\"/>");
            var label = unit == "bps" ? $"{max * g / 4:0.#} G" : $"{max * g / 4:0.#}";
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{Left - 6}\" y=\"{y + 3:0.#}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" text-anchor=\"end\" {Text}>{label}</text>");
        }

        foreach (var hour in new[] { 0, 6, 12, 18, 24 })
        {
            var x = Left + plotW * hour / 24.0;
            var text = DateTime.Now.AddHours(hour - 24).ToString("HH:00", CultureInfo.InvariantCulture);
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{x:0.#}\" y=\"{Top + plotH + 13}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" text-anchor=\"middle\" {Text}>{text}</text>");
        }

        var legendX = Left;
        foreach (var (values, colour, area, label) in series)
        {
            var points = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                var x = Left + plotW * i / (double)(values.Length - 1);
                var y = Top + plotH - plotH * values[i];
                points.Append(CultureInfo.InvariantCulture, $"{x:0.#},{y:0.#} ");
            }

            if (area)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{Left},{Top + plotH} {points}{Left + plotW},{Top + plotH}\" fill=\"{colour}\" fill-opacity=\"0.25\"/>");
            }

            svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{points}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"1.4\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{legendX}\" y=\"{height - 11}\" width=\"8\" height=\"8\" fill=\"{colour}\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{legendX + 12}\" y=\"{height - 4}\" font-family=\"DejaVu Sans Mono, Consolas, monospace\" font-size=\"9\" {Text}>{label}</text>");
            legendX += 60;
        }

        svg.Append("</svg>");
        return svg.ToString();
    }
}
