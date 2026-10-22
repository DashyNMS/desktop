using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Topology;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// Draws a custom map and handles its pointer interaction (#250, from the WPF
/// app's CustomMapCanvas). Everything is drawn in the map's own coordinates
/// (its Width × Height, like LibreNMS's) under one scale and offset, so text
/// and lines zoom with the diagram. Viewing: pan, zoom, click a node for
/// details, double-click to open it. Editing: drag nodes (snapped to the
/// map's grid), link midpoints and the legend; click to select for the
/// properties panel.
/// </summary>
public sealed class CustomMapCanvas : Control
{
    public static readonly StyledProperty<CustomMapDocument?> MapProperty =
        AvaloniaProperty.Register<CustomMapCanvas, CustomMapDocument?>(nameof(Map));

    public static readonly StyledProperty<CustomMapVisuals> VisualsProperty =
        AvaloniaProperty.Register<CustomMapCanvas, CustomMapVisuals>(nameof(Visuals), CustomMapVisuals.Empty);

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<CustomMapCanvas, bool>(nameof(IsEditing));

    public static readonly StyledProperty<string?> SelectedNodeIdProperty =
        AvaloniaProperty.Register<CustomMapCanvas, string?>(nameof(SelectedNodeId));

    public static readonly StyledProperty<string?> SelectedEdgeIdProperty =
        AvaloniaProperty.Register<CustomMapCanvas, string?>(nameof(SelectedEdgeId));

    public static readonly StyledProperty<int> RenderVersionProperty =
        AvaloniaProperty.Register<CustomMapCanvas, int>(nameof(RenderVersion));

    public static readonly StyledProperty<IMapTiles?> TilesProperty =
        AvaloniaProperty.Register<CustomMapCanvas, IMapTiles?>(nameof(Tiles));

    public static readonly StyledProperty<string> TileTemplateProperty =
        AvaloniaProperty.Register<CustomMapCanvas, string>(nameof(TileTemplate), TileUrlTemplate.Default);

    /// <summary>Settings, Maps, "Jiggle physics" (#207, #220).</summary>
    public static readonly StyledProperty<bool> JiggleEnabledProperty =
        AvaloniaProperty.Register<CustomMapCanvas, bool>(nameof(JiggleEnabled));

    /// <summary>The Segoe Fluent Icons glyph the device-image styles use - the app has no copy of LibreNMS's per-OS images.</summary>
    private const string DeviceGlyph = "";

    private const double MinScale = 0.05;
    private const double MaxScale = 8;
    private const double DragThreshold = 3;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Dictionary<string, Rect> _nodeBounds = new();
    private readonly Dictionary<string, Bitmap?> _images = new();
    private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly JiggleSimulation<CustomMapNode> _jiggle;
    private Rect? _legendBounds;

    private double _scale = 1;
    private Vector _offset;
    private Point _pressScreen;
    private Point _lastScreen;
    private bool _moved;
    private DragKind _drag;
    private CustomMapNode? _dragNode;
    private CustomMapEdge? _dragEdge;
    private Vector _grabOffset;

    private enum DragKind
    {
        None,
        Pan,
        Node,
        Midpoint,
        Legend,
    }

    static CustomMapCanvas()
    {
        AffectsRender<CustomMapCanvas>(MapProperty, VisualsProperty, IsEditingProperty, SelectedNodeIdProperty, SelectedEdgeIdProperty, RenderVersionProperty, TileTemplateProperty);
        FocusableProperty.OverrideDefaultValue<CustomMapCanvas>(true);
    }

    public CustomMapCanvas()
    {
        ClipToBounds = true;
        _jiggle = new JiggleSimulation<CustomMapNode>(InvalidateVisual);
    }

    public CustomMapDocument? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public CustomMapVisuals Visuals { get => GetValue(VisualsProperty); set => SetValue(VisualsProperty, value); }

    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    public string? SelectedNodeId { get => GetValue(SelectedNodeIdProperty); set => SetValue(SelectedNodeIdProperty, value); }

    public string? SelectedEdgeId { get => GetValue(SelectedEdgeIdProperty); set => SetValue(SelectedEdgeIdProperty, value); }

    public int RenderVersion { get => GetValue(RenderVersionProperty); set => SetValue(RenderVersionProperty, value); }

    public IMapTiles? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    public string TileTemplate { get => GetValue(TileTemplateProperty); set => SetValue(TileTemplateProperty, value); }

    public bool JiggleEnabled { get => GetValue(JiggleEnabledProperty); set => SetValue(JiggleEnabledProperty, value); }

    /// <summary>A click on empty space, in map coordinates.</summary>
    public event EventHandler<Point>? BackgroundClicked;

    public event EventHandler<string>? NodeClicked;

    public event EventHandler<string>? NodeActivated;

    public event EventHandler<string>? EdgeClicked;

    /// <summary>A node, link midpoint or the legend was dragged to a new place.</summary>
    public event EventHandler? Moved;

    /// <summary>Scales and centres the whole map area in the window.</summary>
    public void FitToView()
    {
        if (Map is not { } map || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        const double margin = 20;
        _scale = Math.Clamp(Math.Min((Bounds.Width - (margin * 2)) / map.Width, (Bounds.Height - (margin * 2)) / map.Height), MinScale, 2);
        _offset = new Vector((Bounds.Width - (map.Width * _scale)) / 2, (Bounds.Height - (map.Height * _scale)) / 2);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MapProperty)
        {
            _images.Clear();
            _jiggle.Clear();
        }
        else if (change.Property == JiggleEnabledProperty)
        {
            _jiggle.IsEnabled = change.GetNewValue<bool>();
        }
        else if (change.Property == TilesProperty)
        {
            if (change.OldValue is IMapTileService old)
            {
                old.TileLoaded -= OnTileLoaded;
            }

            if (change.NewValue is IMapTileService tiles)
            {
                tiles.TileLoaded += OnTileLoaded;
            }
        }
        else if (change.Property == BoundsProperty && change.GetOldValue<Rect>().Width == 0 && change.GetNewValue<Rect>().Width > 0)
        {
            FitToView();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _jiggle.Clear();
    }

    private void OnTileLoaded(object? sender, EventArgs e)
    {
        if (Map?.Background.Type == CustomMapBackgroundType.Map)
        {
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ render

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Resource("BackgroundBrush", Brushes.Black), new Rect(Bounds.Size));
        _nodeBounds.Clear();
        _legendBounds = null;

        if (Map is not { } map)
        {
            return;
        }

        using var view = context.PushTransform(new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y));

        var area = new Rect(0, 0, map.Width, map.Height);
        DrawBackground(context, map, area);

        if (IsEditing)
        {
            DrawGrid(context, map, area);
        }

        var nodes = map.Nodes.ToDictionary(n => n.Id);

        // Nodes are measured first - links end at a node's edge, not its centre.
        var shapes = map.Nodes.ToDictionary(n => n.Id, MeasureNode);

        foreach (var edge in map.Edges)
        {
            if (nodes.TryGetValue(edge.Node1Id, out var a) && nodes.TryGetValue(edge.Node2Id, out var b))
            {
                DrawEdge(context, map, edge, a, b);
            }
        }

        foreach (var node in map.Nodes)
        {
            DrawNode(context, map, node, shapes[node.Id]);
        }

        if (map.Legend.IsVisible)
        {
            DrawLegend(context, map);
        }
    }

    private void DrawBackground(DrawingContext context, CustomMapDocument map, Rect area)
    {
        var border = new Pen(Resource("BorderBrush", Brushes.Gray), 1 / _scale);

        switch (map.Background.Type)
        {
            case CustomMapBackgroundType.Colour:
                context.DrawRectangle(BrushFor(map.Background.Colour), border, area);
                break;

            case CustomMapBackgroundType.Image when ImageFor(map, map.Background.ImageId) is { } image:
                // Stretched to the map area, as LibreNMS does.
                context.DrawImage(image, area);
                context.DrawRectangle(null, border, area);
                break;

            case CustomMapBackgroundType.Map:
                context.DrawRectangle(Resource("SurfaceAltBrush", Brushes.DimGray), null, area);
                using (context.PushClip(area))
                {
                    DrawTiles(context, map);
                }

                context.DrawRectangle(null, border, area);
                break;

            default:
                context.DrawRectangle(Resource("SurfaceBrush", Brushes.Black), border, area);
                break;
        }
    }

    /// <summary>
    /// A geographic background: the map area is a window onto web map tiles
    /// at the chosen zoom, centred on the chosen point, one map pixel per
    /// tile pixel - LibreNMS's "map" background.
    /// </summary>
    private void DrawTiles(DrawingContext context, CustomMapDocument map)
    {
        if (Tiles is not IMapTileService tiles)
        {
            return;
        }

        var zoom = Math.Clamp(map.Background.Zoom, 0, 19);
        var scale = Math.Pow(2, zoom);
        var centre = WebMercator.ToWorld(map.Background.Latitude, map.Background.Longitude);

        var left = (centre.X * scale) - (map.Width / 2.0);
        var top = (centre.Y * scale) - (map.Height / 2.0);
        var count = 1 << zoom;
        var size = WebMercator.TileSize;

        var x0 = Math.Max(0, (int)Math.Floor(left / size));
        var x1 = Math.Min(count - 1, (int)Math.Floor((left + map.Width) / size));
        var y0 = Math.Max(0, (int)Math.Floor(top / size));
        var y1 = Math.Min(count - 1, (int)Math.Floor((top + map.Height) / size));

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (tiles.GetTile(TileTemplate, zoom, x, y) is { } image)
                {
                    context.DrawImage(image, new Rect((x * size) - left, (y * size) - top, size + 0.5, size + 0.5));
                }
            }
        }
    }

    /// <summary>The snapping grid, while editing - thinned out when zoomed far enough out that it'd be a solid wash.</summary>
    private void DrawGrid(DrawingContext context, CustomMapDocument map, Rect area)
    {
        if (map.NodeAlign <= 0)
        {
            return;
        }

        var step = (double)map.NodeAlign;
        while (step * _scale < 12)
        {
            step *= 2;
        }

        var pen = new Pen(Resource("BorderBrush", Brushes.Gray), 1 / _scale);
        using (context.PushOpacity(0.35))
        {
            for (var x = step; x < area.Width; x += step)
            {
                context.DrawLine(pen, new Point(x, 0), new Point(x, area.Height));
            }

            for (var y = step; y < area.Height; y += step)
            {
                context.DrawLine(pen, new Point(0, y), new Point(area.Width, y));
            }
        }
    }

    /// <summary>How big a node is drawn and where its label goes - shared by drawing and hit-testing.</summary>
    private sealed record NodeShape(FormattedText? Label, Size Body, bool LabelInside);

    private NodeShape MeasureNode(CustomMapNode node)
    {
        var visual = Visuals.Nodes.GetValueOrDefault(node.Id);
        var label = string.IsNullOrWhiteSpace(node.Label)
            ? null
            : new FormattedText(node.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFor(node.TextFace)),
                Math.Max(1, node.TextSize), BrushFor(visual?.Text ?? node.TextColour));

        var inside = CustomMapNodeStyles.HasLabelInside(node.Style);
        var textSize = label is null ? default : new Size(label.Width, label.Height);
        const double padding = 6;

        var body = node.Style switch
        {
            CustomMapNodeStyles.Text => textSize,
            CustomMapNodeStyles.Box => new Size(textSize.Width + (padding * 2), textSize.Height + (padding * 2)),
            CustomMapNodeStyles.Ellipse => new Size((textSize.Width + (padding * 2)) * 1.3, (textSize.Height + (padding * 2)) * 1.3),
            CustomMapNodeStyles.Circle => Square(Math.Max(textSize.Width, textSize.Height) + (padding * 2)),
            CustomMapNodeStyles.Database => new Size(textSize.Width + (padding * 2), textSize.Height + (padding * 2) + 12),
            _ => Square(node.Size * 1.2),
        };

        return new NodeShape(label, new Size(Math.Max(body.Width, 8), Math.Max(body.Height, 8)), inside);
    }

    private static Size Square(double side) => new(side, side);

    private void DrawNode(DrawingContext context, CustomMapDocument map, CustomMapNode node, NodeShape shape)
    {
        var visual = Visuals.Nodes.GetValueOrDefault(node.Id) ?? new CustomMapNodeVisual(node.BackgroundColour, node.BorderColour, node.TextColour);
        var fill = BrushFor(visual.Background);
        var pen = node.BorderWidth > 0 ? new Pen(BrushFor(visual.Border), node.BorderWidth) : null;
        var centre = new Point(node.X, node.Y) + _jiggle.Offset(node);
        var stretch = _jiggle.Stretch(node, centre, _scale);
        DrawingContext.PushedState? stretched = stretch is { } matrix ? context.PushTransform(matrix) : null;

        var body = new Rect(centre.X - (shape.Body.Width / 2), centre.Y - (shape.Body.Height / 2), shape.Body.Width, shape.Body.Height);
        var r = Math.Min(body.Width, body.Height) / 2;

        switch (node.Style)
        {
            case CustomMapNodeStyles.Text:
                break;
            case CustomMapNodeStyles.Box:
                context.DrawRectangle(fill, pen, body, 4, 4);
                break;
            case CustomMapNodeStyles.Ellipse:
            case CustomMapNodeStyles.Circle:
            case CustomMapNodeStyles.Dot:
                context.DrawEllipse(fill, pen, centre, body.Width / 2, body.Height / 2);
                break;
            case CustomMapNodeStyles.Database:
                DrawDatabase(context, fill, pen, body);
                break;
            case CustomMapNodeStyles.Square:
                context.DrawRectangle(fill, pen, body);
                break;
            case CustomMapNodeStyles.Diamond:
                context.DrawGeometry(fill, pen, Polygon(centre, r, 4, -90));
                break;
            case CustomMapNodeStyles.Triangle:
                context.DrawGeometry(fill, pen, Polygon(centre, r, 3, -90));
                break;
            case CustomMapNodeStyles.TriangleInverted:
                context.DrawGeometry(fill, pen, Polygon(centre, r, 3, 90));
                break;
            case CustomMapNodeStyles.Hexagon:
                context.DrawGeometry(fill, pen, Polygon(centre, r, 6, 0));
                break;
            case CustomMapNodeStyles.Star:
                context.DrawGeometry(fill, pen, Star(centre, r));
                break;
            case CustomMapNodeStyles.Image when ImageFor(map, node.ImageId) is { } image:
                context.DrawImage(image, Fit(image, body));
                break;
            case CustomMapNodeStyles.Icon:
            case CustomMapNodeStyles.DeviceImage:
            case CustomMapNodeStyles.DeviceImageCircle:
            case CustomMapNodeStyles.Image:
                if (node.Style == CustomMapNodeStyles.DeviceImageCircle)
                {
                    context.DrawEllipse(fill, pen, centre, r, r);
                }
                else
                {
                    context.DrawRectangle(fill, pen, body, 6, 6);
                }

                var glyph = node.Style == CustomMapNodeStyles.Icon ? GlyphFor(node.Icon) : DeviceGlyph;
                var icon = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(IconFont), r * 1.1, BrushFor(visual.Border));
                context.DrawText(icon, new Point(centre.X - (icon.Width / 2), centre.Y - (icon.Height / 2)));
                break;
        }

        if (shape.Label is { } label)
        {
            var origin = shape.LabelInside
                ? new Point(centre.X - (label.Width / 2), centre.Y - (label.Height / 2))
                : new Point(centre.X - (label.Width / 2), body.Bottom + 3);
            context.DrawText(label, origin);

            if (!shape.LabelInside)
            {
                body = body.Union(new Rect(origin, new Size(label.Width, label.Height)));
            }
        }

        _nodeBounds[node.Id] = body;

        if (node.Id == SelectedNodeId)
        {
            var ring = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), 2.5 / _scale);
            context.DrawRectangle(null, ring, body.Inflate(5), 6, 6);
        }

        stretched?.Dispose();
    }

    private static void DrawDatabase(DrawingContext context, IBrush fill, IPen? pen, Rect body)
    {
        const double cap = 6;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(body.Left, body.Top + cap), isFilled: true);
            g.ArcTo(new Point(body.Right, body.Top + cap), new Size(body.Width / 2, cap), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(body.Right, body.Bottom - cap));
            g.ArcTo(new Point(body.Left, body.Bottom - cap), new Size(body.Width / 2, cap), 0, false, SweepDirection.Clockwise);
            g.EndFigure(isClosed: true);
        }

        context.DrawGeometry(fill, pen, geometry);
        context.DrawEllipse(fill, pen, new Point(body.X + (body.Width / 2), body.Top + cap), body.Width / 2, cap);
    }

    private static Geometry Polygon(Point centre, double radius, int sides, double startDegrees) =>
        Closed(Enumerable.Range(0, sides)
            .Select(i => (startDegrees + (360.0 / sides * i)) * Math.PI / 180)
            .Select(a => new Point(centre.X + (radius * Math.Cos(a)), centre.Y + (radius * Math.Sin(a))))
            .ToList());

    private static Geometry Star(Point centre, double radius) =>
        Closed(Enumerable.Range(0, 10)
            .Select(i => (Angle: (-90 + (36.0 * i)) * Math.PI / 180, R: i % 2 == 0 ? radius : radius * 0.45))
            .Select(p => new Point(centre.X + (p.R * Math.Cos(p.Angle)), centre.Y + (p.R * Math.Sin(p.Angle))))
            .ToList());

    private static Geometry Closed(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], isFilled: true);
            foreach (var point in points.Skip(1))
            {
                g.LineTo(point);
            }

            g.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static Rect Fit(Bitmap image, Rect box)
    {
        var ratio = Math.Min(box.Width / image.Size.Width, box.Height / image.Size.Height);
        var size = new Size(image.Size.Width * ratio, image.Size.Height * ratio);
        return new Rect(box.X + ((box.Width - size.Width) / 2), box.Y + ((box.Height - size.Height) / 2), size.Width, size.Height);
    }

    /// <summary>
    /// A link as LibreNMS draws it: two halves meeting at the midpoint (a
    /// little gap between them - the map's edge separation), each coloured
    /// by one direction's traffic, arrows pointing in to the middle (or out
    /// to the ends with "reverse arrows"), labels on each half.
    /// </summary>
    private void DrawEdge(DrawingContext context, CustomMapDocument map, CustomMapEdge edge, CustomMapNode a, CustomMapNode b)
    {
        var visual = Visuals.Edges.GetValueOrDefault(edge.Id) ?? new CustomMapEdgeVisual("#7D8590", "#7D8590", 1.5, 1.5, edge.Label, string.Empty);

        // While either end is still jiggling (#207) the midpoint lags further
        // than the ends, so the link bows like a rubber band.
        var offsetA = _jiggle.Offset(a);
        var offsetB = _jiggle.Offset(b);
        var mid = new Point(edge.MidX, edge.MidY) + ((offsetA + offsetB) * 0.85);
        var from = new Point(a.X, a.Y) + offsetA;
        var to = new Point(b.X, b.Y) + offsetB;
        var gap = map.EdgeSeparation / 2.0;

        if (edge.Id == SelectedEdgeId)
        {
            var glow = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), Math.Max(visual.WidthFrom, visual.WidthTo) + (6 / _scale)) { LineCap = PenLineCap.Round };
            using (context.PushOpacity(0.45))
            {
                context.DrawLine(glow, from, mid);
                context.DrawLine(glow, mid, to);
            }
        }

        DrawHalf(context, map, edge, from, mid, gap, visual.ColourFrom, visual.WidthFrom, visual.LabelFrom);
        DrawHalf(context, map, edge, to, mid, gap, visual.ColourTo, visual.WidthTo, visual.LabelTo);

        if (IsEditing)
        {
            // The midpoint handle - drag it to bend the link.
            var handle = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), 1.5 / _scale);
            context.DrawEllipse(Resource("SurfaceBrush", Brushes.Black), handle, mid, 5 / _scale, 5 / _scale);
        }
    }

    private void DrawHalf(DrawingContext context, CustomMapDocument map, CustomMapEdge edge, Point end, Point mid, double gap, string colour, double width, string label)
    {
        Vector direction = mid - end;
        var length = direction.Length;
        if (length < 1)
        {
            return;
        }

        direction = direction.Normalize();
        var stop = mid - (direction * Math.Min(gap, length / 2));

        var brush = BrushFor(colour);
        var pen = new Pen(brush, Math.Max(0.5, width), edge.LineStyle switch
        {
            CustomMapLineStyle.Dashed => new DashStyle([4.0, 3.0], 0),
            CustomMapLineStyle.Dotted => new DashStyle([1.0, 2.0], 0),
            _ => null,
        });
        context.DrawLine(pen, end, stop);

        // Arrowhead: at the middle end pointing in, or (reversed) at the
        // node end pointing out.
        var arrowTip = map.ReverseArrows ? end + (direction * Math.Min(18, length / 3)) : stop;
        var arrowDir = map.ReverseArrows ? -direction : direction;
        DrawArrow(context, brush, arrowTip, arrowDir, Math.Max(6, width * 3));

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFor(edge.TextFace)),
                Math.Max(1, edge.TextSize), BrushFor(edge.TextColour));
            var at = new Point((end.X + stop.X) / 2, (end.Y + stop.Y) / 2);
            var plate = new Rect(at.X - (text.Width / 2) - 3, at.Y - (text.Height / 2) - 1, text.Width + 6, text.Height + 2);
            using (context.PushOpacity(0.85))
            {
                context.DrawRectangle(Brushes.White, null, plate, 3, 3);
            }

            context.DrawText(text, new Point(plate.X + 3, plate.Y + 1));
        }
    }

    private static void DrawArrow(DrawingContext context, IBrush brush, Point tip, Vector direction, double size)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var back = tip - (direction * size);
        context.DrawGeometry(brush, null, Closed([tip, back + (normal * size * 0.5), back - (normal * size * 0.5)]));
    }

    /// <summary>The link-utilisation legend - LibreNMS's: a header, "Unknown", then the colour steps.</summary>
    private void DrawLegend(DrawingContext context, CustomMapDocument map)
    {
        var legend = map.Legend;
        var fontSize = Math.Max(6, legend.FontSize);
        var rowHeight = fontSize + 10;
        var origin = new Point(legend.X, legend.Y);

        var rows = LinkUtilisation.LegendRows(legend);
        var header = new FormattedText("Legend", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFor(string.Empty), FontStyle.Normal, FontWeight.Bold), fontSize, Brushes.Black);

        // Unknown is black - its text needs to be light to read.
        var labels = rows.Select((row, i) => new FormattedText(row.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(MonoFont), fontSize, i == 0 && !legend.HideInvalid ? Brushes.White : Brushes.Black)).ToList();
        var width = Math.Max(header.Width, labels.Count > 0 ? labels.Max(l => l.Width) : 0) + 16;

        var bounds = new Rect(origin, new Size(width, rowHeight * (rows.Count + 1)));
        context.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), bounds);
        context.DrawText(header, new Point(origin.X + 8, origin.Y + 5));

        for (var i = 0; i < rows.Count; i++)
        {
            var rowRect = new Rect(origin.X + 4, origin.Y + (rowHeight * (i + 1)) + 2, width - 8, rowHeight - 4);
            context.DrawRectangle(BrushFor(rows[i].Colour), null, rowRect);
            context.DrawText(labels[i], new Point(rowRect.X + 4, rowRect.Y + ((rowRect.Height - labels[i].Height) / 2)));
        }

        _legendBounds = bounds;
    }

    // --------------------------------------------------------------- helpers

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private FontFamily MonoFont => this.TryFindResource("MonoFont", ActualThemeVariant, out var font) && font is FontFamily family ? family : new FontFamily("Consolas");

    /// <summary>A node or link's own font, or the app's body font when it has none.</summary>
    private FontFamily FontFor(string face) =>
        !string.IsNullOrWhiteSpace(face) ? new FontFamily(face)
        : this.TryFindResource("BodyFont", ActualThemeVariant, out var font) && font is FontFamily family ? family
        : FontFamily.Default;

    /// <summary>A brush for a "#RRGGBB" (or named) colour, cached - maps reuse a handful of colours many times.</summary>
    private IBrush BrushFor(string? colour)
    {
        var key = string.IsNullOrWhiteSpace(colour) ? "#000000" : colour;
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = Color.TryParse(key, out var parsed) ? new ImmutableSolidColorBrush(parsed) : Brushes.Gray;
            _brushes[key] = brush;
        }

        return brush;
    }

    private static string GlyphFor(string? hex) =>
        int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code is > 0 and < 0x110000
            ? char.ConvertFromUtf32(code)
            : DeviceGlyph;

    /// <summary>Decodes (once) an image embedded in the map.</summary>
    private Bitmap? ImageFor(CustomMapDocument map, string? id)
    {
        if (id is null || !map.Images.TryGetValue(id, out var embedded))
        {
            return null;
        }

        if (_images.TryGetValue(id, out var cached))
        {
            return cached;
        }

        Bitmap? image = null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(embedded.Data));
            image = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            // A broken embedded image just isn't drawn.
        }

        _images[id] = image;
        return image;
    }

    /// <summary>The nodes linked to <paramref name="node"/>, which wobble a little in sympathy when it's dragged.</summary>
    private static IEnumerable<CustomMapNode> LinkedTo(CustomMapDocument map, CustomMapNode node)
    {
        var ids = map.Edges
            .Where(e => e.Node1Id == node.Id || e.Node2Id == node.Id)
            .Select(e => e.Node1Id == node.Id ? e.Node2Id : e.Node1Id)
            .ToHashSet();
        return map.Nodes.Where(n => ids.Contains(n.Id));
    }

    private Point ToMap(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private CustomMapNode? HitNode(Point map)
    {
        if (Map is null)
        {
            return null;
        }

        // Topmost (last drawn) first.
        for (var i = Map.Nodes.Count - 1; i >= 0; i--)
        {
            var node = Map.Nodes[i];
            if (_nodeBounds.TryGetValue(node.Id, out var bounds) && bounds.Inflate(3 / _scale).Contains(map))
            {
                return node;
            }
        }

        return null;
    }

    private CustomMapEdge? HitMidpoint(Point map) =>
        Map?.Edges.FirstOrDefault(e => Point.Distance(new Point(e.MidX, e.MidY), map) <= 8 / _scale);

    private CustomMapEdge? HitEdge(Point map)
    {
        if (Map is null)
        {
            return null;
        }

        var nodes = Map.Nodes.ToDictionary(n => n.Id);
        var reach = 6 / _scale;

        return Map.Edges.FirstOrDefault(e =>
            nodes.TryGetValue(e.Node1Id, out var a) && nodes.TryGetValue(e.Node2Id, out var b)
            && (DistanceToSegment(map, new Point(a.X, a.Y), new Point(e.MidX, e.MidY)) <= reach
                || DistanceToSegment(map, new Point(b.X, b.Y), new Point(e.MidX, e.MidY)) <= reach));
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        var lengthSq = ab.SquaredLength;
        if (lengthSq < 1e-9)
        {
            return Point.Distance(p, a);
        }

        var t = Math.Clamp(Vector.Dot(p - a, ab) / lengthSq, 0, 1);
        return Point.Distance(p, a + (ab * t));
    }

    // ------------------------------------------------------------------- pointer

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();

        var screen = e.GetPosition(this);
        var map = ToMap(screen);
        _pressScreen = screen;
        _lastScreen = screen;
        _moved = false;
        _drag = DragKind.Pan;

        var node = HitNode(map);

        if (e.ClickCount == 2 && node is not null)
        {
            NodeActivated?.Invoke(this, node.Id);
            _drag = DragKind.None;
            e.Handled = true;
            return;
        }

        if (IsEditing)
        {
            if (HitMidpoint(map) is { } midEdge)
            {
                _drag = DragKind.Midpoint;
                _dragEdge = midEdge;
                EdgeClicked?.Invoke(this, midEdge.Id);
            }
            else if (node is not null)
            {
                _drag = DragKind.Node;
                _dragNode = node;
                _grabOffset = new Point(node.X, node.Y) - map;
                NodeClicked?.Invoke(this, node.Id);
            }
            else if (_legendBounds is { } legend && legend.Contains(map))
            {
                _drag = DragKind.Legend;
                _grabOffset = new Point(Map!.Legend.X, Map.Legend.Y) - map;
            }
            else if (HitEdge(map) is { } edge)
            {
                _drag = DragKind.None;
                EdgeClicked?.Invoke(this, edge.Id);
            }
        }
        else if (node is not null)
        {
            _drag = DragKind.None;
            NodeClicked?.Invoke(this, node.Id);
        }

        if (_drag != DragKind.None)
        {
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);

        if (_drag == DragKind.None || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var overSomething = Map is not null && (HitNode(ToMap(screen)) is not null || (IsEditing && HitMidpoint(ToMap(screen)) is not null));
            Cursor = overSomething ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            return;
        }

        if (!_moved && Point.Distance(screen, _pressScreen) < DragThreshold)
        {
            return;
        }

        _moved = true;
        var map = ToMap(screen);

        switch (_drag)
        {
            case DragKind.Pan:
                _offset += screen - _lastScreen;
                break;

            case DragKind.Node when _dragNode is not null && Map is not null:
                var target = map + _grabOffset;
                var (x, y) = SnapToGrid(Map, target.X, target.Y);
                var delta = new Vector(x - _dragNode.X, y - _dragNode.Y);
                _dragNode.X = x;
                _dragNode.Y = y;

                // Links attached to the node keep their shape: each midpoint
                // moves by half as much, so a straight link stays straight.
                // "Recentre links" straightens bent ones.
                foreach (var edge in Map.Edges.Where(ed => ed.Node1Id == _dragNode.Id || ed.Node2Id == _dragNode.Id))
                {
                    edge.MidX += delta.X / 2;
                    edge.MidY += delta.Y / 2;
                }

                _jiggle.Moved(_dragNode, delta, LinkedTo(Map, _dragNode));
                break;

            case DragKind.Midpoint when _dragEdge is not null:
                _dragEdge.MidX = map.X;
                _dragEdge.MidY = map.Y;
                break;

            case DragKind.Legend when Map is not null:
                var legend = map + _grabOffset;
                Map.Legend.X = Math.Max(0, (int)legend.X);
                Map.Legend.Y = Math.Max(0, (int)legend.Y);
                break;
        }

        _lastScreen = screen;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_drag == DragKind.Pan && !_moved)
        {
            BackgroundClicked?.Invoke(this, ToMap(e.GetPosition(this)));
        }
        else if (_moved && _drag is DragKind.Node or DragKind.Midpoint or DragKind.Legend)
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }

        _drag = DragKind.None;
        _dragNode = null;
        _dragEdge = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var screen = e.GetPosition(this);
        var before = ToMap(screen);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), MinScale, MaxScale);
        _offset = new Vector(screen.X - (before.X * _scale), screen.Y - (before.Y * _scale));
        InvalidateVisual();
        e.Handled = true;
    }

    private static (double X, double Y) SnapToGrid(CustomMapDocument map, double x, double y)
    {
        var grid = map.NodeAlign;
        return grid > 0 ? (Math.Round(x / grid) * grid, Math.Round(y / grid) * grid) : (x, y);
    }
}
