using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Topology;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// Draws a custom map and handles its mouse interaction. Everything is drawn
/// in the map's own coordinates (its Width × Height, like LibreNMS's) under
/// one scale/offset, so text and lines zoom with the diagram. Viewing: pan,
/// zoom, click a node for details, double-click to open it. Editing: drag
/// nodes (snapped to the map's grid), link midpoints and the legend; click
/// to select for the properties panel.
/// </summary>
public sealed class CustomMapCanvas : FrameworkElement
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(CustomMapDocument), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CustomMapCanvas)d)._images.Clear()));

    public static readonly DependencyProperty VisualsProperty = DependencyProperty.Register(
        nameof(Visuals), typeof(CustomMapVisuals), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(CustomMapVisuals.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedNodeIdProperty = DependencyProperty.Register(
        nameof(SelectedNodeId), typeof(string), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedEdgeIdProperty = DependencyProperty.Register(
        nameof(SelectedEdgeId), typeof(string), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RenderVersionProperty = DependencyProperty.Register(
        nameof(RenderVersion), typeof(int), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles), typeof(IMapTileService), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(null, OnTilesChanged));

    public static readonly DependencyProperty TileTemplateProperty = DependencyProperty.Register(
        nameof(TileTemplate), typeof(string), typeof(CustomMapCanvas),
        new FrameworkPropertyMetadata(TileUrlTemplate.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The Segoe Fluent Icons glyph the device-image styles use - the app has no copy of LibreNMS's per-OS images.</summary>
    private const string DeviceGlyph = "\uE839";

    private const double MinScale = 0.05;
    private const double MaxScale = 8;
    private const double DragThreshold = 3;

    private readonly Dictionary<string, Rect> _nodeBounds = new();
    private readonly Dictionary<string, BitmapSource?> _images = new();
    private readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private Rect _legendBounds = Rect.Empty;

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

    public CustomMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public CustomMapDocument? Map
    {
        get => (CustomMapDocument?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public CustomMapVisuals Visuals
    {
        get => (CustomMapVisuals)GetValue(VisualsProperty);
        set => SetValue(VisualsProperty, value);
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public string? SelectedNodeId
    {
        get => (string?)GetValue(SelectedNodeIdProperty);
        set => SetValue(SelectedNodeIdProperty, value);
    }

    public string? SelectedEdgeId
    {
        get => (string?)GetValue(SelectedEdgeIdProperty);
        set => SetValue(SelectedEdgeIdProperty, value);
    }

    public int RenderVersion
    {
        get => (int)GetValue(RenderVersionProperty);
        set => SetValue(RenderVersionProperty, value);
    }

    public IMapTileService? Tiles
    {
        get => (IMapTileService?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    public string TileTemplate
    {
        get => (string)GetValue(TileTemplateProperty);
        set => SetValue(TileTemplateProperty, value);
    }

    /// <summary>A click on empty space, in map coordinates.</summary>
    public event EventHandler<Point>? BackgroundClicked;

    public event EventHandler<string>? NodeClicked;

    public event EventHandler<string>? NodeActivated;

    public event EventHandler<string>? EdgeClicked;

    /// <summary>A node, link midpoint or the legend was dragged to a new place.</summary>
    public event EventHandler? Moved;

    private static void OnTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (CustomMapCanvas)d;
        if (e.OldValue is IMapTileService old)
        {
            old.TileLoaded -= canvas.OnTileLoaded;
        }

        if (e.NewValue is IMapTileService tiles)
        {
            tiles.TileLoaded += canvas.OnTileLoaded;
        }
    }

    private void OnTileLoaded(object? sender, EventArgs e)
    {
        if (Map?.Background.Type == CustomMapBackgroundType.Map)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Scales and centres the whole map area in the window.</summary>
    public void FitToView()
    {
        if (Map is not { } map || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        const double margin = 20;
        _scale = Math.Clamp(Math.Min((ActualWidth - margin * 2) / map.Width, (ActualHeight - margin * 2) / map.Height), MinScale, 2);
        _offset = new Vector((ActualWidth - map.Width * _scale) / 2, (ActualHeight - map.Height * _scale) / 2);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width == 0 && sizeInfo.NewSize.Width > 0)
        {
            FitToView();
        }
    }

    // ------------------------------------------------------------------ render

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Resource("BackgroundBrush", Brushes.Black), null, new Rect(0, 0, ActualWidth, ActualHeight));
        _nodeBounds.Clear();
        _legendBounds = Rect.Empty;

        if (Map is not { } map)
        {
            return;
        }

        dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y));

        var area = new Rect(0, 0, map.Width, map.Height);
        DrawBackground(dc, map, area);

        if (IsEditing)
        {
            DrawGrid(dc, map, area);
        }

        var nodes = map.Nodes.ToDictionary(n => n.Id);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Nodes are measured first - links end at a node's edge, not its centre.
        var shapes = map.Nodes.ToDictionary(n => n.Id, n => MeasureNode(n, dpi));

        foreach (var edge in map.Edges)
        {
            if (nodes.TryGetValue(edge.Node1Id, out var a) && nodes.TryGetValue(edge.Node2Id, out var b))
            {
                DrawEdge(dc, map, edge, a, b, dpi);
            }
        }

        foreach (var node in map.Nodes)
        {
            DrawNode(dc, map, node, shapes[node.Id], dpi);
        }

        if (map.Legend.IsVisible)
        {
            DrawLegend(dc, map, dpi);
        }

        dc.Pop();
    }

    private void DrawBackground(DrawingContext dc, CustomMapDocument map, Rect area)
    {
        var border = new Pen(Resource("BorderBrush", Brushes.Gray), 1 / _scale);

        switch (map.Background.Type)
        {
            case CustomMapBackgroundType.Colour:
                dc.DrawRectangle(BrushFor(map.Background.Colour), border, area);
                break;

            case CustomMapBackgroundType.Image when ImageFor(map, map.Background.ImageId) is { } image:
                // Stretched to the map area, as LibreNMS does.
                dc.DrawImage(image, area);
                dc.DrawRectangle(null, border, area);
                break;

            case CustomMapBackgroundType.Map:
                dc.DrawRectangle(Resource("SurfaceAltBrush", Brushes.DimGray), null, area);
                dc.PushClip(new RectangleGeometry(area));
                DrawTiles(dc, map);
                dc.Pop();
                dc.DrawRectangle(null, border, area);
                break;

            default:
                dc.DrawRectangle(Resource("SurfaceBrush", Brushes.Black), border, area);
                break;
        }
    }

    /// <summary>
    /// A geographic background: the map area is a window onto web map tiles
    /// at the chosen zoom, centred on the chosen point, one map pixel per
    /// tile pixel - LibreNMS's "map" background.
    /// </summary>
    private void DrawTiles(DrawingContext dc, CustomMapDocument map)
    {
        if (Tiles is not { } tiles)
        {
            return;
        }

        var zoom = Math.Clamp(map.Background.Zoom, 0, 19);
        var scale = Math.Pow(2, zoom);
        var centre = WebMercator.ToWorld(map.Background.Latitude, map.Background.Longitude);
        var cx = centre.X * scale;
        var cy = centre.Y * scale;

        var left = cx - map.Width / 2.0;
        var top = cy - map.Height / 2.0;
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
                    dc.DrawImage(image, new Rect(x * size - left, y * size - top, size + 0.5, size + 0.5));
                }
            }
        }
    }

    /// <summary>The snapping grid, while editing - thinned out when zoomed far enough out that it'd be a solid wash.</summary>
    private void DrawGrid(DrawingContext dc, CustomMapDocument map, Rect area)
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
        dc.PushOpacity(0.35);
        for (var x = step; x < area.Width; x += step)
        {
            dc.DrawLine(pen, new Point(x, 0), new Point(x, area.Height));
        }

        for (var y = step; y < area.Height; y += step)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(area.Width, y));
        }

        dc.Pop();
    }

    /// <summary>How big a node is drawn and where its label goes - shared by drawing and hit-testing.</summary>
    private sealed record NodeShape(FormattedText? Label, Size Body, bool LabelInside);

    private NodeShape MeasureNode(CustomMapNode node, double dpi)
    {
        var visual = Visuals.Nodes.GetValueOrDefault(node.Id);
        var label = string.IsNullOrWhiteSpace(node.Label)
            ? null
            : new FormattedText(
                node.Label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFor(node.TextFace), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                Math.Max(1, node.TextSize),
                BrushFor(visual?.Text ?? node.TextColour),
                dpi);

        var inside = CustomMapNodeStyles.HasLabelInside(node.Style);
        var textSize = label is null ? new Size(0, 0) : new Size(label.Width, label.Height);
        const double padding = 6;

        var body = node.Style switch
        {
            CustomMapNodeStyles.Text => textSize,
            CustomMapNodeStyles.Box => new Size(textSize.Width + padding * 2, textSize.Height + padding * 2),
            CustomMapNodeStyles.Ellipse => new Size((textSize.Width + padding * 2) * 1.3, (textSize.Height + padding * 2) * 1.3),
            CustomMapNodeStyles.Circle => Square(Math.Max(textSize.Width, textSize.Height) + padding * 2),
            CustomMapNodeStyles.Database => new Size(textSize.Width + padding * 2, textSize.Height + padding * 2 + 12),
            _ => Square(node.Size * 1.2),
        };

        return new NodeShape(label, new Size(Math.Max(body.Width, 8), Math.Max(body.Height, 8)), inside);
    }

    private static Size Square(double side) => new(side, side);

    private void DrawNode(DrawingContext dc, CustomMapDocument map, CustomMapNode node, NodeShape shape, double dpi)
    {
        var visual = Visuals.Nodes.GetValueOrDefault(node.Id) ?? new CustomMapNodeVisual(node.BackgroundColour, node.BorderColour, node.TextColour);
        var fill = BrushFor(visual.Background);
        var pen = node.BorderWidth > 0 ? new Pen(BrushFor(visual.Border), node.BorderWidth) : null;
        var centre = new Point(node.X, node.Y);
        var body = new Rect(centre.X - shape.Body.Width / 2, centre.Y - shape.Body.Height / 2, shape.Body.Width, shape.Body.Height);
        var r = Math.Min(body.Width, body.Height) / 2;

        switch (node.Style)
        {
            case CustomMapNodeStyles.Text:
                break;
            case CustomMapNodeStyles.Box:
                dc.DrawRoundedRectangle(fill, pen, body, 4, 4);
                break;
            case CustomMapNodeStyles.Ellipse:
            case CustomMapNodeStyles.Circle:
            case CustomMapNodeStyles.Dot:
                dc.DrawEllipse(fill, pen, centre, body.Width / 2, body.Height / 2);
                break;
            case CustomMapNodeStyles.Database:
                DrawDatabase(dc, fill, pen, body);
                break;
            case CustomMapNodeStyles.Square:
                dc.DrawRectangle(fill, pen, body);
                break;
            case CustomMapNodeStyles.Diamond:
                dc.DrawGeometry(fill, pen, Polygon(centre, r, 4, -90));
                break;
            case CustomMapNodeStyles.Triangle:
                dc.DrawGeometry(fill, pen, Polygon(centre, r, 3, -90));
                break;
            case CustomMapNodeStyles.TriangleInverted:
                dc.DrawGeometry(fill, pen, Polygon(centre, r, 3, 90));
                break;
            case CustomMapNodeStyles.Hexagon:
                dc.DrawGeometry(fill, pen, Polygon(centre, r, 6, 0));
                break;
            case CustomMapNodeStyles.Star:
                dc.DrawGeometry(fill, pen, Star(centre, r));
                break;
            case CustomMapNodeStyles.Image when ImageFor(map, node.ImageId) is { } image:
                dc.DrawImage(image, Fit(image, body));
                break;
            case CustomMapNodeStyles.Icon:
            case CustomMapNodeStyles.DeviceImage:
            case CustomMapNodeStyles.DeviceImageCircle:
            case CustomMapNodeStyles.Image:
                if (node.Style == CustomMapNodeStyles.DeviceImageCircle)
                {
                    dc.DrawEllipse(fill, pen, centre, r, r);
                }
                else
                {
                    dc.DrawRoundedRectangle(fill, pen, body, 6, 6);
                }

                var glyph = node.Style == CustomMapNodeStyles.Icon ? GlyphFor(node.Icon) : DeviceGlyph;
                var icon = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), r * 1.1, BrushFor(visual.Border), dpi);
                dc.DrawText(icon, new Point(centre.X - icon.Width / 2, centre.Y - icon.Height / 2));
                break;
        }

        if (shape.Label is { } label)
        {
            var origin = shape.LabelInside
                ? new Point(centre.X - label.Width / 2, centre.Y - label.Height / 2)
                : new Point(centre.X - label.Width / 2, body.Bottom + 3);
            dc.DrawText(label, origin);

            if (!shape.LabelInside)
            {
                body.Union(new Rect(origin, new Size(label.Width, label.Height)));
            }
        }

        _nodeBounds[node.Id] = body;

        if (node.Id == SelectedNodeId)
        {
            var ring = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), 2.5 / _scale);
            var highlight = body;
            highlight.Inflate(5, 5);
            dc.DrawRoundedRectangle(null, ring, highlight, 6, 6);
        }
    }

    private static void DrawDatabase(DrawingContext dc, Brush fill, Pen? pen, Rect body)
    {
        const double cap = 6;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(body.Left, body.Top + cap), true, true);
            g.ArcTo(new Point(body.Right, body.Top + cap), new Size(body.Width / 2, cap), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(new Point(body.Right, body.Bottom - cap), true, false);
            g.ArcTo(new Point(body.Left, body.Bottom - cap), new Size(body.Width / 2, cap), 0, false, SweepDirection.Clockwise, true, false);
        }

        dc.DrawGeometry(fill, pen, geometry);
        dc.DrawEllipse(fill, pen, new Point(body.X + body.Width / 2, body.Top + cap), body.Width / 2, cap);
    }

    private static Geometry Polygon(Point centre, double radius, int sides, double startDegrees)
    {
        var points = Enumerable.Range(0, sides)
            .Select(i => (startDegrees + 360.0 / sides * i) * Math.PI / 180)
            .Select(a => new Point(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a)))
            .ToList();
        return Closed(points);
    }

    private static Geometry Star(Point centre, double radius)
    {
        var points = Enumerable.Range(0, 10)
            .Select(i => (Angle: (-90 + 36.0 * i) * Math.PI / 180, R: i % 2 == 0 ? radius : radius * 0.45))
            .Select(p => new Point(centre.X + p.R * Math.Cos(p.Angle), centre.Y + p.R * Math.Sin(p.Angle)))
            .ToList();
        return Closed(points);
    }

    private static Geometry Closed(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], true, true);
            g.PolyLineTo(points.Skip(1).ToList(), true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Rect Fit(ImageSource image, Rect box)
    {
        var ratio = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var size = new Size(image.Width * ratio, image.Height * ratio);
        return new Rect(box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2, size.Width, size.Height);
    }

    /// <summary>
    /// A link as LibreNMS draws it: two halves meeting at the midpoint (a
    /// little gap between them - the map's edge separation), each coloured
    /// by one direction's traffic, arrows pointing in to the middle (or out
    /// to the ends with "reverse arrows"), labels on each half.
    /// </summary>
    private void DrawEdge(DrawingContext dc, CustomMapDocument map, CustomMapEdge edge, CustomMapNode a, CustomMapNode b, double dpi)
    {
        var visual = Visuals.Edges.GetValueOrDefault(edge.Id) ?? new CustomMapEdgeVisual("#7D8590", "#7D8590", 1.5, 1.5, edge.Label, string.Empty);
        var mid = new Point(edge.MidX, edge.MidY);
        var from = new Point(a.X, a.Y);
        var to = new Point(b.X, b.Y);
        var gap = map.EdgeSeparation / 2.0;

        if (edge.Id == SelectedEdgeId)
        {
            var glow = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), Math.Max(visual.WidthFrom, visual.WidthTo) + 6 / _scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.PushOpacity(0.45);
            dc.DrawLine(glow, from, mid);
            dc.DrawLine(glow, mid, to);
            dc.Pop();
        }

        DrawHalf(dc, map, edge, from, mid, gap, visual.ColourFrom, visual.WidthFrom, visual.LabelFrom, dpi);
        DrawHalf(dc, map, edge, to, mid, gap, visual.ColourTo, visual.WidthTo, visual.LabelTo, dpi);

        if (IsEditing)
        {
            // The midpoint handle - drag it to bend the link.
            var handle = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), 1.5 / _scale);
            dc.DrawEllipse(Resource("SurfaceBrush", Brushes.Black), handle, mid, 5 / _scale, 5 / _scale);
        }
    }

    private void DrawHalf(DrawingContext dc, CustomMapDocument map, CustomMapEdge edge, Point end, Point mid, double gap, string colour, double width, string label, double dpi)
    {
        var direction = mid - end;
        var length = direction.Length;
        if (length < 1)
        {
            return;
        }

        direction.Normalize();
        var stop = mid - direction * Math.Min(gap, length / 2);

        var brush = BrushFor(colour);
        var pen = new Pen(brush, Math.Max(0.5, width))
        {
            DashStyle = edge.LineStyle switch
            {
                CustomMapLineStyle.Dashed => new DashStyle(new[] { 4.0, 3.0 }, 0),
                CustomMapLineStyle.Dotted => new DashStyle(new[] { 1.0, 2.0 }, 0),
                _ => DashStyles.Solid,
            },
        };
        dc.DrawLine(pen, end, stop);

        // Arrowhead: at the middle end pointing in, or (reversed) at the
        // node end pointing out.
        var arrowTip = map.ReverseArrows ? end + direction * Math.Min(18, length / 3) : stop;
        var arrowDir = map.ReverseArrows ? -direction : direction;
        DrawArrow(dc, brush, arrowTip, arrowDir, Math.Max(6, width * 3));

        if (!string.IsNullOrWhiteSpace(label))
        {
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFor(edge.TextFace), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Math.Max(1, edge.TextSize), BrushFor(edge.TextColour), dpi);
            var at = end + (stop - end) * 0.5;
            var plate = new Rect(at.X - text.Width / 2 - 3, at.Y - text.Height / 2 - 1, text.Width + 6, text.Height + 2);
            dc.PushOpacity(0.85);
            dc.DrawRoundedRectangle(Brushes.White, null, plate, 3, 3);
            dc.Pop();
            dc.DrawText(text, new Point(plate.X + 3, plate.Y + 1));
        }
    }

    private static void DrawArrow(DrawingContext dc, Brush brush, Point tip, Vector direction, double size)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var back = tip - direction * size;
        var geometry = Closed(new[] { tip, back + normal * size * 0.5, back - normal * size * 0.5 });
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>The link-utilisation legend - LibreNMS's: a header, "Unknown", then the colour steps.</summary>
    private void DrawLegend(DrawingContext dc, CustomMapDocument map, double dpi)
    {
        var legend = map.Legend;
        var fontSize = Math.Max(6, legend.FontSize);
        var rowHeight = fontSize + 10;
        var origin = new Point(legend.X, legend.Y);

        var rows = LinkUtilisation.LegendRows(legend);
        var header = new FormattedText("Legend", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), fontSize, Brushes.Black, dpi);

        var labels = rows.Select(r => new FormattedText(r.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), fontSize, Brushes.Black, dpi)).ToList();
        var width = Math.Max(header.Width, labels.Count > 0 ? labels.Max(l => l.Width) : 0) + 16;

        var bounds = new Rect(origin, new Size(width, rowHeight * (rows.Count + 1)));
        dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), bounds);
        dc.DrawText(header, new Point(origin.X + 8, origin.Y + 5));

        for (var i = 0; i < rows.Count; i++)
        {
            var rowRect = new Rect(origin.X + 4, origin.Y + rowHeight * (i + 1) + 2, width - 8, rowHeight - 4);
            dc.DrawRectangle(BrushFor(rows[i].Colour), null, rowRect);

            // Unknown is black - its text needs to be light to read.
            var text = i == 0 && !legend.HideInvalid ? Recolour(labels[i], Brushes.White) : labels[i];
            dc.DrawText(text, new Point(rowRect.X + 4, rowRect.Y + (rowRect.Height - text.Height) / 2));
        }

        _legendBounds = bounds;
    }

    private static FormattedText Recolour(FormattedText text, Brush brush)
    {
        text.SetForegroundBrush(brush);
        return text;
    }

    // --------------------------------------------------------------- helpers

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    /// <summary>A brush for a "#RRGGBB" (or named) colour, cached - maps reuse a handful of colours many times.</summary>
    private Brush BrushFor(string? colour)
    {
        var key = string.IsNullOrWhiteSpace(colour) ? "#000000" : colour;
        if (_brushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Brush brush;
        try
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key));
        }
        catch (FormatException)
        {
            brush = Brushes.Gray;
        }

        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    private static FontFamily FontFor(string face) => new(string.IsNullOrWhiteSpace(face) ? "Segoe UI" : face);

    private static string GlyphFor(string? hex) =>
        int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code is > 0 and < 0x110000
            ? char.ConvertFromUtf32(code)
            : DeviceGlyph;

    /// <summary>Decodes (once) an image embedded in the map.</summary>
    private BitmapSource? ImageFor(CustomMapDocument map, string? id)
    {
        if (id is null || !map.Images.TryGetValue(id, out var embedded))
        {
            return null;
        }

        if (_images.TryGetValue(id, out var cached))
        {
            return cached;
        }

        BitmapSource? image = null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(embedded.Data));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            // A broken embedded image just isn't drawn.
        }

        _images[id] = image;
        return image;
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
            if (_nodeBounds.TryGetValue(node.Id, out var bounds))
            {
                var hit = bounds;
                hit.Inflate(3 / _scale, 3 / _scale);
                if (hit.Contains(map))
                {
                    return node;
                }
            }
        }

        return null;
    }

    private CustomMapEdge? HitMidpoint(Point map) =>
        Map?.Edges.FirstOrDefault(e => (new Point(e.MidX, e.MidY) - map).Length <= 8 / _scale);

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
        var ab = b - a;
        var lengthSq = ab.LengthSquared;
        if (lengthSq < 1e-9)
        {
            return (p - a).Length;
        }

        var t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSq, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    // ------------------------------------------------------------------- mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
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
            else if (_legendBounds.Contains(map))
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
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var screen = e.GetPosition(this);

        if (_drag == DragKind.None || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = Map is not null && (HitNode(ToMap(screen)) is not null || (IsEditing && HitMidpoint(ToMap(screen)) is not null))
                ? Cursors.Hand
                : Cursors.Arrow;
            return;
        }

        if (!_moved && (screen - _pressScreen).Length < DragThreshold)
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

                // Links attached to the node keep their shape: each
                // midpoint moves by half as much, so a straight link stays
                // straight. "Recentre links" straightens bent ones.
                foreach (var edge in Map.Edges.Where(ed => ed.Node1Id == _dragNode.Id || ed.Node2Id == _dragNode.Id))
                {
                    edge.MidX += delta.X / 2;
                    edge.MidY += delta.Y / 2;
                }

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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

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
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var screen = e.GetPosition(this);
        var before = ToMap(screen);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta / 120.0), MinScale, MaxScale);
        _offset = new Vector(screen.X - before.X * _scale, screen.Y - before.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }

    private static (double X, double Y) SnapToGrid(CustomMapDocument map, double x, double y)
    {
        var grid = map.NodeAlign;
        return grid > 0 ? (Math.Round(x / grid) * grid, Math.Round(y / grid) * grid) : (x, y);
    }
}
