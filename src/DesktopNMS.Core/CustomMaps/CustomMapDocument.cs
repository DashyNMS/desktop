using System.Text.Json.Serialization;

namespace DesktopNMS.Core.CustomMaps;

/// <summary>
/// A custom map - a hand-drawn diagram of devices and links, mirroring
/// LibreNMS's own Custom Maps (its custom_maps / custom_map_nodes /
/// custom_map_edges tables, same options and defaults). Stored as one
/// self-contained ".map" JSON file per map (see <see cref="CustomMapStore"/>),
/// with any images embedded, so the file alone can be exported and
/// imported. Devices and ports are referenced by LibreNMS id only, so a
/// map is tied to the server it was drawn against.
/// </summary>
public sealed class CustomMapDocument
{
    /// <summary>Bumped if the file format changes incompatibly - an older app refuses a newer file rather than misreading it.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Stable identity, also the file name - see <see cref="CustomMapStore"/>.</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "New map";

    /// <summary>Maps with the same group are listed together - LibreNMS's menu_group.</summary>
    public string? MenuGroup { get; set; }

    /// <summary>The drawing area, in map pixels - LibreNMS's width/height (default 1800 × 800).</summary>
    public int Width { get; set; } = 1800;

    public int Height { get; set; } = 800;

    /// <summary>Grid that nodes snap to while being dragged, in map pixels; 0 turns snapping off - LibreNMS's node_align.</summary>
    public int NodeAlign { get; set; } = 10;

    /// <summary>Arrows point from the middle of a link out to each end, instead of from each end in.</summary>
    public bool ReverseArrows { get; set; }

    /// <summary>Gap, in map pixels, left between the two halves of a link at its midpoint.</summary>
    public int EdgeSeparation { get; set; } = 10;

    public CustomMapBackground Background { get; set; } = new();

    public CustomMapLegend Legend { get; set; } = new();

    /// <summary>Settings a newly added node starts with - LibreNMS's newnodeconfig.</summary>
    public CustomMapNode NodeDefaults { get; set; } = CustomMapNode.CreateDefault();

    /// <summary>Settings a newly added link starts with - LibreNMS's newedgeconfig.</summary>
    public CustomMapEdge EdgeDefaults { get; set; } = CustomMapEdge.CreateDefault();

    public List<CustomMapNode> Nodes { get; set; } = new();

    public List<CustomMapEdge> Edges { get; set; } = new();

    /// <summary>Images the map uses (background, node images), embedded so the file is self-contained. Keyed by an id nodes/background refer to.</summary>
    public Dictionary<string, CustomMapImage> Images { get; set; } = new();

    public DateTimeOffset Modified { get; set; } = DateTimeOffset.Now;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>A deep copy - the editor works on one, so Cancel can simply throw it away.</summary>
    public CustomMapDocument Clone() => CustomMapSerializer.Deserialize(CustomMapSerializer.Serialize(this));

    /// <summary>Removes images nothing refers to any more, so replaced images don't bloat the file forever.</summary>
    public void PruneImages()
    {
        var used = Nodes.Select(n => n.ImageId).Append(Background.ImageId).Where(id => id is not null).ToHashSet();
        foreach (var id in Images.Keys.Where(k => !used.Contains(k)).ToList())
        {
            Images.Remove(id);
        }
    }
}

public enum CustomMapBackgroundType
{
    None,
    Colour,
    Image,

    /// <summary>A geographic map (web map tiles) centred on <see cref="CustomMapBackground.Latitude"/>/<see cref="CustomMapBackground.Longitude"/> - LibreNMS's "map" background.</summary>
    Map,
}

public sealed class CustomMapBackground
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomMapBackgroundType Type { get; set; }

    public string Colour { get; set; } = "#FFFFFF";

    /// <summary>Key into <see cref="CustomMapDocument.Images"/> when <see cref="Type"/> is Image.</summary>
    public string? ImageId { get; set; }

    public double Latitude { get; set; } = 51.48;

    public double Longitude { get; set; }

    public int Zoom { get; set; } = 10;
}

/// <summary>The link-utilisation legend - LibreNMS's legend_x/y/steps/font_size/hide_invalid/hide_overspeed/colours.</summary>
public sealed class CustomMapLegend
{
    /// <summary>Map position of the legend's top-left corner; a negative X hides it (as in LibreNMS).</summary>
    public int X { get; set; } = -1;

    public int Y { get; set; } = -1;

    /// <summary>How many percentage steps the default (gradient) legend shows.</summary>
    public int Steps { get; set; } = 7;

    public int FontSize { get; set; } = 14;

    /// <summary>Leave out the "Unknown" row (links with no speed/port data).</summary>
    public bool HideInvalid { get; set; }

    /// <summary>Scale the legend to 100% rather than 150% (don't show overspeed).</summary>
    public bool HideOverspeed { get; set; }

    /// <summary>
    /// Optional fixed colour steps, keyed by percentage ("0", "50", ...),
    /// plus "-1" for unknown and "-2" for down - LibreNMS's legend_colours.
    /// Null uses the smooth green→yellow→red→purple gradient instead.
    /// </summary>
    public Dictionary<string, string>? Colours { get; set; }

    public bool IsVisible => X >= 0;
}

/// <summary>
/// LibreNMS's node styles (custom_map.node_type), including the device
/// image ones - drawn here with a generic device symbol, as the app has no
/// copy of LibreNMS's OS images.
/// </summary>
public static class CustomMapNodeStyles
{
    public const string Box = "box";
    public const string Circle = "circle";
    public const string Database = "database";
    public const string Ellipse = "ellipse";
    public const string Text = "text";
    public const string DeviceImage = "device_image";
    public const string DeviceImageCircle = "device_image_circle";
    public const string Diamond = "diamond";
    public const string Dot = "dot";
    public const string Star = "star";
    public const string Triangle = "triangle";
    public const string TriangleInverted = "triangle_inverted";
    public const string Hexagon = "hexagon";
    public const string Square = "square";
    public const string Icon = "icon";

    /// <summary>A node drawn with one of the map's embedded images.</summary>
    public const string Image = "image";

    public static IReadOnlyList<(string Value, string Label)> All { get; } = new[]
    {
        (Box, "Box"),
        (Circle, "Circle"),
        (Database, "Database"),
        (Ellipse, "Ellipse"),
        (Text, "Text"),
        (DeviceImage, "Device image"),
        (DeviceImageCircle, "Device image (circular)"),
        (Diamond, "Diamond"),
        (Dot, "Dot"),
        (Star, "Star"),
        (Triangle, "Triangle"),
        (TriangleInverted, "Triangle inverted"),
        (Hexagon, "Hexagon"),
        (Square, "Square"),
        (Icon, "Icon"),
        (Image, "Image"),
    };

    /// <summary>Shapes whose label sits inside them (like vis.js) rather than below.</summary>
    public static bool HasLabelInside(string style) => style is Box or Circle or Database or Ellipse or Text;
}

public sealed class CustomMapNode
{
    public string Id { get; set; } = CustomMapDocument.NewId();

    public string Label { get; set; } = string.Empty;

    /// <summary>The LibreNMS device this node represents - colours it by that device's state, and opens it on double-click.</summary>
    public int? DeviceId { get; set; }

    /// <summary>Another custom map this node links to - shown as down if anything on that map is down, and opens it on double-click.</summary>
    public string? LinkedMapId { get; set; }

    public string Style { get; set; } = CustomMapNodeStyles.Box;

    /// <summary>Segoe Fluent Icons glyph for the Icon style, e.g. "E968".</summary>
    public string? Icon { get; set; }

    /// <summary>Key into <see cref="CustomMapDocument.Images"/> for the Image style.</summary>
    public string? ImageId { get; set; }

    public int Size { get; set; } = 25;

    public int BorderWidth { get; set; } = 1;

    public string TextFace { get; set; } = "arial";

    public int TextSize { get; set; } = 14;

    public string TextColour { get; set; } = "#343434";

    public string BackgroundColour { get; set; } = "#D2E5FF";

    public string BorderColour { get; set; } = "#2B7CE9";

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>LibreNMS's out-of-the-box node settings.</summary>
    public static CustomMapNode CreateDefault() => new();

    /// <summary>A fresh node (new id, no device/link, no position) with this one's look - for "new node from defaults".</summary>
    public CustomMapNode CopyStyle() => new()
    {
        Style = Style,
        Icon = Icon,
        ImageId = ImageId,
        Size = Size,
        BorderWidth = BorderWidth,
        TextFace = TextFace,
        TextSize = TextSize,
        TextColour = TextColour,
        BackgroundColour = BackgroundColour,
        BorderColour = BorderColour,
    };
}

public enum CustomMapLineStyle
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>
/// A link between two nodes. Drawn, like LibreNMS's, as two halves meeting
/// at a movable midpoint: with a port set, each half is coloured by that
/// direction's utilisation (see <see cref="LinkUtilisation"/>).
/// </summary>
public sealed class CustomMapEdge
{
    public string Id { get; set; } = CustomMapDocument.NewId();

    public string Node1Id { get; set; } = string.Empty;

    public string Node2Id { get; set; } = string.Empty;

    /// <summary>The port whose traffic colours this link; null for a plain line.</summary>
    public int? PortId { get; set; }

    /// <summary>Swap which half shows in and which shows out - for a port on the far end of the link.</summary>
    public bool Reverse { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CustomMapLineStyle LineStyle { get; set; }

    public bool ShowPercent { get; set; } = true;

    public bool ShowBps { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>A fixed line width; null sizes it by link speed, as LibreNMS does.</summary>
    public double? FixedWidth { get; set; }

    public string TextFace { get; set; } = "arial";

    public int TextSize { get; set; } = 12;

    public string TextColour { get; set; } = "#343434";

    /// <summary>The midpoint the two halves meet at - dragged in the editor; "recentred" puts it back halfway.</summary>
    public double MidX { get; set; }

    public double MidY { get; set; }

    public static CustomMapEdge CreateDefault() => new();

    public CustomMapEdge CopyStyle() => new()
    {
        LineStyle = LineStyle,
        ShowPercent = ShowPercent,
        ShowBps = ShowBps,
        FixedWidth = FixedWidth,
        TextFace = TextFace,
        TextSize = TextSize,
        TextColour = TextColour,
    };
}

/// <summary>An image embedded in a map file.</summary>
public sealed class CustomMapImage
{
    /// <summary>e.g. "image/png".</summary>
    public string MimeType { get; set; } = "image/png";

    /// <summary>The image bytes, base64.</summary>
    public string Data { get; set; } = string.Empty;
}
