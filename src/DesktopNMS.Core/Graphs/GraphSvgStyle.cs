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

/// <summary>A restyled graph, and the colour each of its series was given, in drawing order.</summary>
public sealed record StyledGraph(string Svg, IReadOnlyList<string> SeriesColours);

/// <summary>
/// Restyles a graph SVG as LibreNMS renders it (rrdtool's Cairo output) into
/// the app's own look: horizontal dashed gridlines in the border colour, no
/// vertical or minor grid, muted axis and legend text, and each series in one
/// of the app's colours - a line in full, its area see-through beneath.
/// Nothing is redrawn: rrdtool's own paths are kept and only their colours,
/// opacity and dashes change, so it works for every graph type and whatever
/// font the server has. Series can be left out, for a legend that turns them
/// on and off.
/// </summary>
/// <remarks>
/// rrdtool writes every colour as an attribute - <c>fill="rgb(64.7%, 64.7%, 64.7%)"</c> -
/// and draws text as glyph outlines inside a <c>&lt;g fill="…"&gt;</c>.
/// LibreNMS's API always draws in its light palette (it never passes the
/// dark "style"): black text, minor grid #a5a5a5, major grid #FF9999, frame
/// and arrows #5e5e5e, rrdtool's own axis #1F1F1F. Those are recognised
/// exactly; anything else greyish is told apart by how light it is.
/// <para>
/// A series is a line, an area, or both, drawn one after another (a sensor
/// graph: a line per sensor; processors: a line then its area per core;
/// traffic: an area then its line per direction). LibreNMS's colours repeat,
/// so series are told apart by drawing order: a new one starts at a new hue,
/// or a second line (or area) in another colour. Each gets its own app
/// colour - the first the accent, as in the mockups, the rest by hue.
/// </para>
/// </remarks>
public static partial class GraphSvgStyle
{
    private static readonly (byte R, byte G, byte B) MinorGrid = (0xA5, 0xA5, 0xA5);
    private static readonly (byte R, byte G, byte B) MajorGrid = (0xFF, 0x99, 0x99);
    private static readonly (byte R, byte G, byte B) Frame = (0x5E, 0x5E, 0x5E);
    private static readonly (byte R, byte G, byte B) Axis = (0x1F, 0x1F, 0x1F);

    /// <summary>The restyled SVG; anything that isn't an rrdtool SVG comes back unchanged.</summary>
    public static string Apply(string svg, GraphPalette palette) => Restyle(svg, palette).Svg;

    /// <summary>
    /// The restyled SVG and its series' colours, leaving out the series in
    /// <paramref name="hidden"/> (indexes in drawing order). A hidden series
    /// keeps its colour for the legend; the others keep theirs too.
    /// </summary>
    public static StyledGraph Restyle(string svg, GraphPalette palette, IReadOnlySet<int>? hidden = null)
    {
        if (string.IsNullOrEmpty(svg) || !svg.Contains("<svg", StringComparison.Ordinal))
        {
            return new StyledGraph(svg, Array.Empty<string>());
        }

        // Text: rrdtool's glyph outlines grouped under one fill, or plain <text>.
        svg = TextFill().Replace(svg, m => IsDark(m.Groups["c"].Value) ? m.Groups["head"].Value + palette.Text + "\"" : m.Value);

        var paths = PathElement().Matches(svg).Select(m => Describe(m)).ToList();
        var groups = GroupSeries(paths);
        var colours = AssignColours(groups, palette);

        // A legend's colour square takes the colour of the series it stands for.
        var swatchColours = new Dictionary<(byte, byte, byte), string>();
        foreach (var path in paths.Where(p => p.Series is not null))
        {
            swatchColours.TryAdd(path.Colour!.Value, colours[path.Series!.Value]);
        }

        var index = 0;
        return new StyledGraph(
            PathElement().Replace(svg, _ => Restyle(paths[index++], palette, colours, swatchColours, hidden) ?? string.Empty),
            colours);
    }

    /// <summary>What one &lt;path&gt; is: furniture, a series' line or area, or a legend square.</summary>
    private sealed class PathInfo(string markup)
    {
        public string Markup { get; } = markup;

        public Role Role { get; init; }

        public bool IsLine { get; init; }

        public (byte R, byte G, byte B)? Colour { get; init; }

        public bool IsSwatch { get; init; }

        /// <summary>The series this path belongs to, once grouped.</summary>
        public int? Series { get; set; }
    }

    private static PathInfo Describe(Match match)
    {
        var path = match.Value;
        if (Attribute(path, "stroke") is { } stroke && Parse(stroke) is { } strokeColour)
        {
            return new PathInfo(path) { Role = RoleOf(strokeColour), IsLine = true, Colour = strokeColour };
        }

        if (Attribute(path, "fill") is { } fill && fill != "none" && Parse(fill) is { } fillColour)
        {
            return new PathInfo(path) { Role = RoleOf(fillColour), IsLine = false, Colour = fillColour, IsSwatch = IsSwatch(path) };
        }

        return new PathInfo(path) { Role = Role.Other };
    }

    /// <summary>Splits the series paths into series, in drawing order; returns each series' family.</summary>
    private static List<Family> GroupSeries(List<PathInfo> paths)
    {
        var families = new List<Family>();
        Family? family = null;
        (byte R, byte G, byte B)? line = null, area = null;

        foreach (var path in paths.Where(p => p.Role == Role.Series && !p.IsSwatch && !IsInvisible(p.Markup)))
        {
            var colour = path.Colour!.Value;
            var pathFamily = FamilyOf(colour);
            var sameKindBefore = path.IsLine ? line : area;
            var continues = family == pathFamily && (sameKindBefore is null || sameKindBefore == colour);

            if (!continues)
            {
                families.Add(pathFamily);
                family = pathFamily;
                line = area = null;
            }

            if (path.IsLine)
            {
                line = colour;
            }
            else
            {
                area = colour;
            }

            path.Series = families.Count - 1;
        }

        return families;
    }

    /// <summary>
    /// An app colour per series: the first takes the accent; the rest keep
    /// their own hue where it's free, a blue one taking what the first gave up
    /// (so traffic is In blue / Out green) - never alarm red for a blue one
    /// after a red first (a CPU graph) - and the next free colour otherwise.
    /// </summary>
    private static List<string> AssignColours(List<Family> families, GraphPalette palette)
    {
        var colours = new List<string>();
        var spare = new[] { palette.Ok, palette.Purple, palette.Teal, palette.Orange, palette.Pink, palette.Warning, palette.Critical };

        for (var i = 0; i < families.Count; i++)
        {
            if (i == 0)
            {
                colours.Add(palette.Accent);
                continue;
            }

            var family = families[i];
            var wanted = family != Family.Blue ? Natural(family, palette)
                : families[0] == Family.Red ? palette.Teal
                : Natural(families[0], palette);

            if (colours.Contains(wanted))
            {
                wanted = spare.FirstOrDefault(c => !colours.Contains(c)) ?? spare[(i - 1) % spare.Length];
            }

            colours.Add(wanted);
        }

        return colours;
    }

    private static string Natural(Family family, GraphPalette palette) => family switch
    {
        Family.Red => palette.Critical,
        Family.Orange => palette.Orange,
        Family.Yellow => palette.Warning,
        Family.Green => palette.Ok,
        Family.Teal => palette.Teal,
        Family.Purple => palette.Purple,
        Family.Pink => palette.Pink,
        Family.Blue => palette.Accent,

        // Grey: the accent's own place, taken by the first series - so the next free colour.
        _ => palette.Ok,
    };

    /// <summary>One &lt;path&gt;, restyled - or null to drop it (minor and vertical grid, a hidden series).</summary>
    private static string? Restyle(PathInfo info, GraphPalette palette, List<string> colours, Dictionary<(byte, byte, byte), string> swatchColours, IReadOnlySet<int>? hidden)
    {
        var path = info.Markup;
        var colourAttribute = info.IsLine ? "stroke" : "fill";

        switch (info.Role)
        {
            case Role.MinorGrid:
                return info.IsLine ? null : SetAttribute(path, "fill", palette.Grid);
            case Role.MajorGrid:
                if (!info.IsLine)
                {
                    return SetAttribute(path, "fill", palette.Grid);
                }

                // Vertical time divisions and the short tick marks go; the horizontal lines stay, dashed.
                if (!IsHorizontal(path) || IsShort(path))
                {
                    return null;
                }

                path = SetAttribute(path, "stroke", palette.Grid);
                path = SetAttribute(path, "stroke-width", "1");
                return SetAttribute(path, "stroke-dasharray", "3 5");
            case Role.Frame:
                return SetAttribute(path, colourAttribute, palette.Grid);
            case Role.Text:
                return SetAttribute(path, colourAttribute, palette.Text);
            case Role.Series when info.IsSwatch:
                return SetAttribute(path, "fill", swatchColours.GetValueOrDefault(info.Colour!.Value, palette.Accent));
            case Role.Series when info.Series is { } series:
                if (hidden is not null && hidden.Contains(series))
                {
                    return null;
                }

                path = SetAttribute(path, colourAttribute, colours[series]);
                if (info.IsLine)
                {
                    return Width(path) is { } width && width >= 1 && width < 1.6 ? SetAttribute(path, "stroke-width", "1.6") : path;
                }

                return Opacity(path, "fill-opacity") == 0
                    ? path
                    : SetAttribute(path, "fill-opacity", Format(Math.Min(Opacity(path, "fill-opacity") ?? 1, palette.AreaOpacity)));
            default:
                return path;
        }
    }

    private enum Role
    {
        Other,
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
        return IsGrey(c) && lightness is > 0.45 and < 0.65 ? Role.Frame : Role.Series;
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

    private static Family FamilyOf((byte R, byte G, byte B) c)
    {
        if (IsGrey(c))
        {
            return Family.Grey;
        }

        return Hsl(c).Hue switch
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

    /// <summary>A path drawn fully transparent - LibreNMS's invisible "LINE:min#00000000" under the ping band.</summary>
    private static bool IsInvisible(string path)
        => Opacity(path, "stroke-opacity") == 0 || Opacity(path, "fill-opacity") == 0;

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
