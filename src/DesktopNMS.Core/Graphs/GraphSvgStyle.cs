using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Graphs;

/// <summary>
/// The colours a restyled graph is drawn in - the app's own, so LibreNMS's
/// graphs match the rest of it. Hex colours ("#3B82F6"); each app fills it
/// from its current theme.
/// </summary>
public sealed record GraphPalette(
    string Text,
    string Grid,
    string Accent,
    string Ok,
    string Warning,
    string Critical,
    string Purple,
    string Teal,
    string Pink,
    string Orange)
{
    /// <summary>The dark theme's colours, as in the desktop and mobile mockups.</summary>
    public static GraphPalette Dark { get; } = new(
        Text: "#98A2B3",
        Grid: "#2C3442",
        Accent: "#3B82F6",
        Ok: "#2EA043",
        Warning: "#DB9A04",
        Critical: "#DA3633",
        Purple: "#A371F7",
        Teal: "#2DB8B0",
        Pink: "#E05FA0",
        Orange: "#F0883E");

    /// <summary>How see-through a series' filled area is, under its line.</summary>
    public double AreaOpacity { get; init; } = 0.22;
}

/// <summary>
/// Restyles a graph SVG as LibreNMS renders it (rrdtool's Cairo output) into
/// the app's own look: horizontal dashed gridlines in the border colour, no
/// vertical or minor grid, muted axis and legend text, and each series in the
/// nearest of the app's colours - a line in full, its area see-through
/// beneath. Nothing is redrawn: rrdtool's own paths are kept and only their
/// colours, opacity and dashes change, so it works for every graph type and
/// whatever font the server has.
/// </summary>
/// <remarks>
/// rrdtool writes every colour as an attribute - <c>fill="rgb(64.7%, 64.7%, 64.7%)"</c> -
/// and draws text as glyph outlines inside a <c>&lt;g fill="…"&gt;</c>.
/// LibreNMS's API always draws in its light palette (it never passes the
/// dark "style"): black text, minor grid #a5a5a5, major grid #FF9999, frame
/// and arrows #5e5e5e, rrdtool's own axis #1F1F1F. Those are recognised
/// exactly; anything else greyish is told apart by how light it is, and any
/// colour by its hue.
/// </remarks>
public static partial class GraphSvgStyle
{
    private static readonly (byte R, byte G, byte B) MinorGrid = (0xA5, 0xA5, 0xA5);
    private static readonly (byte R, byte G, byte B) MajorGrid = (0xFF, 0x99, 0x99);
    private static readonly (byte R, byte G, byte B) Frame = (0x5E, 0x5E, 0x5E);
    private static readonly (byte R, byte G, byte B) Axis = (0x1F, 0x1F, 0x1F);

    /// <summary>The restyled SVG; anything that isn't an rrdtool SVG comes back unchanged.</summary>
    public static string Apply(string svg, GraphPalette palette)
    {
        if (string.IsNullOrEmpty(svg) || !svg.Contains("<svg", StringComparison.Ordinal))
        {
            return svg;
        }

        // Text: rrdtool's glyph outlines grouped under one fill, or plain <text>.
        svg = TextFill().Replace(svg, m => IsDark(m.Groups["c"].Value) ? m.Groups["head"].Value + palette.Text + "\"" : m.Value);

        // Paths come in drawing order - grid, then each series as defined - so
        // the first series met is the graph's main one.
        var series = new SeriesColours(palette);
        return PathElement().Replace(svg, m => RestylePath(m.Value, palette, series) ?? string.Empty);
    }

    /// <summary>One &lt;path&gt;, restyled - or null to drop it (minor and vertical grid).</summary>
    private static string? RestylePath(string path, GraphPalette palette, SeriesColours series)
    {
        var stroke = Attribute(path, "stroke");
        var fill = Attribute(path, "fill");

        if (stroke is not null && Parse(stroke) is { } strokeColour)
        {
            var role = RoleOf(strokeColour);
            switch (role)
            {
                case Role.MinorGrid:
                    return null;
                case Role.MajorGrid:
                    // Vertical time divisions and the short tick marks go; the horizontal lines stay, dashed.
                    if (!IsHorizontal(path) || IsShort(path))
                    {
                        return null;
                    }

                    path = SetAttribute(path, "stroke", palette.Grid);
                    path = SetAttribute(path, "stroke-width", "1");
                    return SetAttribute(path, "stroke-dasharray", "3 5");
                case Role.Frame:
                    return SetAttribute(path, "stroke", palette.Grid);
                case Role.Text:
                    return SetAttribute(path, "stroke", palette.Text);
                case Role.Series:
                    path = SetAttribute(path, "stroke", series.For(strokeColour));
                    return Width(path) is { } width && width >= 1 && width < 1.6
                        ? SetAttribute(path, "stroke-width", "1.6")
                        : path;
            }
        }

        if (fill is not null && fill != "none" && Parse(fill) is { } fillColour)
        {
            switch (RoleOf(fillColour))
            {
                case Role.Text:
                    return SetAttribute(path, "fill", palette.Text);
                case Role.Frame or Role.MinorGrid or Role.MajorGrid:
                    return SetAttribute(path, "fill", palette.Grid);
                case Role.Series:
                    path = SetAttribute(path, "fill", series.For(fillColour));

                    // A series' area sits see-through under its line; a legend swatch stays solid.
                    return IsSwatch(path) || Opacity(path, "fill-opacity") == 0
                        ? path
                        : SetAttribute(path, "fill-opacity", Format(Math.Min(Opacity(path, "fill-opacity") ?? 1, palette.AreaOpacity)));
            }
        }

        return path;
    }

    private enum Role
    {
        Text,
        MinorGrid,
        MajorGrid,
        Frame,
        Series,
    }

    private static Role RoleOf((byte R, byte G, byte B) c)
    {
        if (c == MinorGrid)
        {
            return Role.MinorGrid;
        }

        if (c == MajorGrid)
        {
            return Role.MajorGrid;
        }

        if (c == Frame || c == Axis)
        {
            return Role.Frame;
        }

        if (c is (0, 0, 0))
        {
            return Role.Text;
        }

        // Other greys: a mid grey is furniture (a zero line, a frame);
        // a dark or light one is a series (LibreNMS's ping line is #36393d,
        // its jitter band #ccd2de).
        var (_, _, lightness) = Hsl(c);
        if (IsGrey(c) && lightness is > 0.45 and < 0.65)
        {
            return Role.Frame;
        }

        return Role.Series;
    }

    /// <summary>A family of series colours: a hue, or grey. A series' line and area are one family.</summary>
    private enum Family
    {
        Grey,
        Red,
        Orange,
        Yellow,
        Green,
        Teal,
        Blue,
        Purple,
        Pink,
    }

    /// <summary>
    /// Gives each family of series colours an app colour, in the order the
    /// series are drawn. The first - the graph's main series - takes the
    /// accent, as in the mockups; the rest keep their own hue, and a blue one
    /// (the accent's own) takes whatever the first one gave up. So a port's
    /// traffic is In blue / Out green, a CPU graph isn't alarm red, and ping's
    /// loss stays red.
    /// </summary>
    private sealed class SeriesColours(GraphPalette palette)
    {
        private readonly Dictionary<Family, string> _assigned = new();
        private Family? _first;

        public string For((byte R, byte G, byte B) colour)
        {
            var family = FamilyOf(colour);
            if (_assigned.TryGetValue(family, out var assigned))
            {
                return assigned;
            }

            if (_first is null)
            {
                _first = family;
                return _assigned[family] = palette.Accent;
            }

            var own = family == Family.Blue ? Natural(_first.Value) : Natural(family);
            return _assigned[family] = own;
        }

        private string Natural(Family family) => family switch
        {
            Family.Red => palette.Critical,
            Family.Orange => palette.Orange,
            Family.Yellow => palette.Warning,
            Family.Green => palette.Ok,
            Family.Teal => palette.Teal,
            Family.Purple => palette.Purple,
            Family.Pink => palette.Pink,

            // Grey or blue first: the accent's taken, so the next free colour.
            _ => palette.Ok,
        };

        private static Family FamilyOf((byte R, byte G, byte B) c)
        {
            var (hue, _, _) = Hsl(c);
            if (IsGrey(c))
            {
                return Family.Grey;
            }

            return hue switch
            {
                < 15 or >= 345 => Family.Red,
                < 40 => Family.Orange,
                < 70 => Family.Yellow,
                < 160 => Family.Green,
                < 195 => Family.Teal,
                < 255 => Family.Blue,
                < 290 => Family.Purple,
                _ => Family.Pink,
            };
        }
    }

    // ------------------------------------------------------------- geometry

    private static bool IsHorizontal(string path)
        => PathPoints(path) is [var a, var b, ..] && Math.Abs(a.Y - b.Y) < 0.01;

    private static bool IsShort(string path)
        => PathPoints(path) is [var a, var b, ..] && Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 6;

    /// <summary>A legend's colour square: a closed path no more than about 10px across.</summary>
    private static bool IsSwatch(string path)
    {
        var points = PathPoints(path);
        if (points.Count < 4)
        {
            return false;
        }

        var width = points.Max(p => p.X) - points.Min(p => p.X);
        var height = points.Max(p => p.Y) - points.Min(p => p.Y);
        return width is > 0 and <= 10 && height is > 0 and <= 10;
    }

    private static List<(double X, double Y)> PathPoints(string path)
    {
        var d = Attribute(path, "d");
        var points = new List<(double X, double Y)>();
        if (d is null)
        {
            return points;
        }

        foreach (Match m in PointPair().Matches(d))
        {
            points.Add((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            if (points.Count >= 64)
            {
                break;
            }
        }

        return points;
    }

    // --------------------------------------------------------------- colour

    /// <summary>
    /// Grey, or nearly: the channels within about 10% of each other. Chroma, not
    /// HSL saturation, which calls a very light or dark tint (LibreNMS's jitter
    /// band, #ccd2de) strongly coloured.
    /// </summary>
    private static bool IsGrey((byte R, byte G, byte B) c)
        => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 26;

    private static bool IsDark(string rgb) => Parse(rgb) is { } c && Hsl(c).Lightness < 0.2;

    private static (byte R, byte G, byte B)? Parse(string value)
    {
        var m = RgbPercent().Match(value);
        if (m.Success)
        {
            return (Channel(m.Groups[1].Value), Channel(m.Groups[2].Value), Channel(m.Groups[3].Value));
        }

        if (value.Length == 7 && value[0] == '#'
            && byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (r, g, b);
        }

        return null;

        static byte Channel(string percent)
            => (byte)Math.Clamp(Math.Round(double.Parse(percent, CultureInfo.InvariantCulture) * 255 / 100), 0, 255);
    }

    private static (double Hue, double Saturation, double Lightness) Hsl((byte R, byte G, byte B) c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var delta = max - min;
        if (delta < 1e-9)
        {
            return (0, 0, lightness);
        }

        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        return (hue < 0 ? hue + 360 : hue, saturation, lightness);
    }

    // ----------------------------------------------------------- attributes

    private static string? Attribute(string element, string name)
    {
        var m = Regex.Match(element, $"\\s{Regex.Escape(name)}=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string SetAttribute(string element, string name, string value)
    {
        var pattern = $"\\s{Regex.Escape(name)}=\"[^\"]*\"";
        if (Regex.IsMatch(element, pattern))
        {
            return Regex.Replace(element, pattern, $" {name}=\"{value}\"");
        }

        var end = element.EndsWith("/>", StringComparison.Ordinal) ? element.Length - 2 : element.Length - 1;
        return element[..end].TrimEnd() + $" {name}=\"{value}\"" + element[end..];
    }

    private static double? Opacity(string element, string name)
        => Attribute(element, name) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : null;

    private static double? Width(string element) => Opacity(element, "stroke-width");

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex("(?<head><(?:g|text)\\b[^>]*?\\sfill=\")(?<c>rgb\\([^)]*\\)|#[0-9A-Fa-f]{6})\"")]
    private static partial Regex TextFill();

    [GeneratedRegex("<path\\b[^>]*>")]
    private static partial Regex PathElement();

    [GeneratedRegex("rgb\\(\\s*([\\d.]+)%\\s*,\\s*([\\d.]+)%\\s*,\\s*([\\d.]+)%\\s*\\)")]
    private static partial Regex RgbPercent();

    [GeneratedRegex("(-?[\\d.]+)\\s+(-?[\\d.]+)")]
    private static partial Regex PointPair();
}
