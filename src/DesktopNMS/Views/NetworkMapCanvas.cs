using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.Converters;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// Draws the network map (issue #56) and handles its mouse interaction:
/// wheel to zoom (around the cursor), drag the background to pan, drag a
/// node to move it, click to select, double-click to open the device.
/// Drawn directly in OnRender - hundreds of nodes and lines as individual
/// WPF elements would be far slower to lay out and to pan - with node
/// positions in map units, mapped to the screen by one scale and offset.
/// </summary>
public sealed class NetworkMapCanvas : FrameworkElement
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(
        nameof(Nodes), typeof(IReadOnlyList<MapNode>), typeof(NetworkMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NetworkMapCanvas)d)._labels.Clear()));

    public static readonly DependencyProperty EdgesProperty = DependencyProperty.Register(
        nameof(Edges), typeof(IReadOnlyList<MapEdge>), typeof(NetworkMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(MapNode), typeof(NetworkMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Bump to redraw when node names/states change in place (the view model's RenderVersion).</summary>
    public static readonly DependencyProperty RenderVersionProperty = DependencyProperty.Register(
        nameof(RenderVersion), typeof(int), typeof(NetworkMapCanvas),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NetworkMapCanvas)d)._labels.Clear()));

    private const double MinScale = 0.05;
    private const double MaxScale = 4;

    /// <summary>Below this zoom, only the selected, hovered and neighbouring nodes are labelled - a label on every node would just be noise.</summary>
    private const double LabelAllScale = 0.75;

    /// <summary>How far (screen pixels) the mouse must move before a press becomes a drag rather than a click.</summary>
    private const double DragThreshold = 3;

    private readonly Dictionary<MapNode, FormattedText> _labels = new();

    private double _scale = 1;
    private Vector _offset;
    private MapNode? _hover;
    private MapNode? _dragNode;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _isPanning;
    private bool _moved;

    public NetworkMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<MapNode>? Nodes
    {
        get => (IReadOnlyList<MapNode>?)GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public IReadOnlyList<MapEdge>? Edges
    {
        get => (IReadOnlyList<MapEdge>?)GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    public MapNode? SelectedNode
    {
        get => (MapNode?)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public int RenderVersion
    {
        get => (int)GetValue(RenderVersionProperty);
        set => SetValue(RenderVersionProperty, value);
    }

    /// <summary>Raised when the user drops a node they dragged - the view model saves the layout.</summary>
    public event EventHandler<MapNode>? NodeMoved;

    /// <summary>Raised on double-clicking a node.</summary>
    public event EventHandler<MapNode>? NodeActivated;

    /// <summary>Zooms and pans so every node fits, with a margin; leaves small maps at no more than 1:1.</summary>
    public void FitToView()
    {
        if (Nodes is not { Count: > 0 } nodes || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var left = nodes.Min(n => n.X);
        var right = nodes.Max(n => n.X);
        var top = nodes.Min(n => n.Y);
        var bottom = nodes.Max(n => n.Y);

        const double margin = 60;
        var width = Math.Max(right - left, 1);
        var height = Math.Max(bottom - top, 1);
        _scale = Math.Clamp(Math.Min((ActualWidth - margin * 2) / width, (ActualHeight - margin * 2) / height), MinScale, 1);

        var centre = new Point((left + right) / 2, (top + bottom) / 2);
        _offset = new Vector(ActualWidth / 2 - centre.X * _scale, ActualHeight / 2 - centre.Y * _scale);
        InvalidateVisual();
    }

    /// <summary>Pans (without changing zoom, unless zoomed right out) so this node is in the middle.</summary>
    public void CenterOn(MapNode node)
    {
        if (_scale < LabelAllScale)
        {
            _scale = 1;
        }

        _offset = new Vector(ActualWidth / 2 - node.X * _scale, ActualHeight / 2 - node.Y * _scale);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // First real size after being hidden (the tab wasn't shown yet):
        // there was nothing to fit against until now.
        if (sizeInfo.PreviousSize.Width == 0 && sizeInfo.NewSize.Width > 0)
        {
            FitToView();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so empty space is hit-testable for panning.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (Nodes is not { Count: > 0 } nodes)
        {
            return;
        }

        var edges = Edges ?? Array.Empty<MapEdge>();
        var selected = SelectedNode;
        var lineBrush = Resource("TextSecondaryBrush", Brushes.Gray);
        var accent = Resource("AccentBrush", Brushes.DodgerBlue);
        var surface = Resource("SurfaceBrush", Brushes.Black);

        var neighbours = selected is null
            ? new HashSet<MapNode>()
            : edges.Where(e => e.Touches(selected)).Select(e => ReferenceEquals(e.A, selected) ? e.B : e.A).ToHashSet();

        // Everything not connected to the selection is dimmed, so its
        // connections stand out on a busy map.
        var dimOthers = selected is not null;

        foreach (var edge in edges)
        {
            var highlighted = selected is not null && edge.Touches(selected);
            var thickness = (edge.LinkCount > 1 ? 2.5 : 1.2) * (highlighted ? 1.6 : 1);
            var pen = new Pen(highlighted ? accent : lineBrush, thickness);

            // A link to a device that's down is dotted.
            if (edge.IsToOfflineDevice)
            {
                pen.DashStyle = new DashStyle(new[] { 1.0, 2.5 }, 0);
                pen.DashCap = PenLineCap.Round;
            }

            dc.PushOpacity(highlighted ? 1 : dimOthers ? 0.15 : 0.5);
            dc.DrawLine(pen, ToScreen(edge.A), ToScreen(edge.B));
            dc.Pop();
        }

        var radius = NodeRadius;
        var outline = new Pen(surface, 1.5);
        var selectedRing = new Pen(accent, 3);

        foreach (var node in nodes)
        {
            var centre = ToScreen(node);
            var faded = dimOthers && !ReferenceEquals(node, selected) && !neighbours.Contains(node);

            dc.PushOpacity(faded ? 0.3 : 1);
            if (node.IsAccessPoint)
            {
                // An access point isn't a LibreNMS device - smaller, and square.
                var half = radius * 0.7;
                dc.DrawRoundedRectangle(StateBrush(node.State), outline, new Rect(centre.X - half, centre.Y - half, half * 2, half * 2), 2, 2);
                if (ReferenceEquals(node, selected))
                {
                    dc.DrawRoundedRectangle(null, selectedRing, new Rect(centre.X - half - 4, centre.Y - half - 4, (half + 4) * 2, (half + 4) * 2), 3, 3);
                }
            }
            else
            {
                dc.DrawEllipse(StateBrush(node.State), outline, centre, radius, radius);
                if (ReferenceEquals(node, selected))
                {
                    dc.DrawEllipse(null, selectedRing, centre, radius + 4, radius + 4);
                }
            }

            dc.Pop();
        }

        // Labels last, so no node is drawn over one.
        var labelAll = _scale >= LabelAllScale;
        foreach (var node in nodes)
        {
            var isFocus = ReferenceEquals(node, selected) || ReferenceEquals(node, _hover);
            if (!labelAll && !isFocus && !neighbours.Contains(node))
            {
                continue;
            }

            if (dimOthers && !isFocus && !neighbours.Contains(node))
            {
                continue;
            }

            DrawLabel(dc, node, radius, isFocus, surface);
        }
    }

    private void DrawLabel(DrawingContext dc, MapNode node, double radius, bool emphasise, Brush surface)
    {
        if (!_labels.TryGetValue(node, out var text))
        {
            text = new FormattedText(
                node.Name,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                Resource("TextPrimaryBrush", Brushes.White),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            _labels[node] = text;
        }

        var centre = ToScreen(node);
        var origin = new Point(centre.X - text.Width / 2, centre.Y + radius + 3);

        if (emphasise)
        {
            // A backing plate so the hovered/selected label reads over lines.
            var plate = new Rect(origin.X - 4, origin.Y - 1, text.Width + 8, text.Height + 2);
            dc.PushOpacity(0.85);
            dc.DrawRoundedRectangle(surface, null, plate, 3, 3);
            dc.Pop();
        }

        dc.DrawText(text, origin);
    }

    /// <summary>Node size tracks zoom, within limits - still findable zoomed out, not huge zoomed in.</summary>
    private double NodeRadius => Math.Clamp(8 * _scale, 3.5, 13);

    private static Brush StateBrush(DeviceState state) => state switch
    {
        DeviceState.Up => SeverityToBrushConverter.Ok,
        DeviceState.Down => SeverityToBrushConverter.Critical,
        DeviceState.Maintenance => SeverityToBrushConverter.Maintenance,
        _ => SeverityToBrushConverter.Unknown,
    };

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private Point ToScreen(MapNode node) => new(node.X * _scale + _offset.X, node.Y * _scale + _offset.Y);

    private Point ToMap(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private MapNode? HitTest(Point screen)
    {
        if (Nodes is not { } nodes)
        {
            return null;
        }

        var reach = NodeRadius + 4;
        MapNode? best = null;
        var bestDistance = double.MaxValue;

        foreach (var node in nodes)
        {
            var distance = (ToScreen(node) - screen).Length;
            if (distance <= reach && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var point = e.GetPosition(this);
        var hit = HitTest(point);

        if (e.ClickCount == 2 && hit is not null)
        {
            NodeActivated?.Invoke(this, hit);
            e.Handled = true;
            return;
        }

        _pressPoint = point;
        _lastPoint = point;
        _moved = false;

        if (hit is not null)
        {
            SelectedNode = hit;
            _dragNode = hit;
        }
        else
        {
            _isPanning = true;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (_dragNode is not null || _isPanning)
        {
            if (!_moved && (point - _pressPoint).Length < DragThreshold)
            {
                return;
            }

            _moved = true;

            if (_dragNode is not null)
            {
                var map = ToMap(point);
                _dragNode.X = map.X;
                _dragNode.Y = map.Y;
            }
            else
            {
                _offset += point - _lastPoint;
            }

            _lastPoint = point;
            InvalidateVisual();
            return;
        }

        var hover = HitTest(point);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragNode is not null && _moved)
        {
            NodeMoved?.Invoke(this, _dragNode);
        }
        else if (_isPanning && !_moved)
        {
            // A plain click on empty space clears the selection.
            SelectedNode = null;
        }

        _dragNode = null;
        _isPanning = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var point = e.GetPosition(this);
        var before = ToMap(point);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta / 120.0), MinScale, MaxScale);

        // Keep the point under the cursor fixed while zooming.
        _offset = new Vector(point.X - before.X * _scale, point.Y - before.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }
}
