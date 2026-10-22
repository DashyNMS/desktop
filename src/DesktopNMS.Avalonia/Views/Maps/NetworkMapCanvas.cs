using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// Draws the network map (#56, #250) and handles its pointer interaction:
/// wheel to zoom (around the cursor), drag the background to pan, drag a
/// node to move it, click to select, double-click to open the device.
/// Drawn directly in Render - hundreds of nodes and lines as controls would
/// be far slower to lay out and pan - with node positions in map units,
/// mapped to the screen by one scale and offset.
/// </summary>
public sealed class NetworkMapCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<MapNode>?> NodesProperty =
        AvaloniaProperty.Register<NetworkMapCanvas, IReadOnlyList<MapNode>?>(nameof(Nodes));

    public static readonly StyledProperty<IReadOnlyList<MapEdge>?> EdgesProperty =
        AvaloniaProperty.Register<NetworkMapCanvas, IReadOnlyList<MapEdge>?>(nameof(Edges));

    public static readonly StyledProperty<MapNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<NetworkMapCanvas, MapNode?>(nameof(SelectedNode), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Bump to redraw when node names/states change in place (the view model's RenderVersion).</summary>
    public static readonly StyledProperty<int> RenderVersionProperty =
        AvaloniaProperty.Register<NetworkMapCanvas, int>(nameof(RenderVersion));

    /// <summary>Settings, Maps, "Jiggle physics" (#207, #220).</summary>
    public static readonly StyledProperty<bool> JiggleEnabledProperty =
        AvaloniaProperty.Register<NetworkMapCanvas, bool>(nameof(JiggleEnabled));

    private const double MinScale = 0.05;
    private const double MaxScale = 4;

    /// <summary>Below this zoom, only the selected, hovered and neighbouring nodes are labelled - a label on every node would just be noise.</summary>
    private const double LabelAllScale = 0.75;

    /// <summary>How far (screen pixels) the pointer must move before a press becomes a drag rather than a click.</summary>
    private const double DragThreshold = 3;

    private readonly Dictionary<MapNode, FormattedText> _labels = new();
    private readonly JiggleSimulation<MapNode> _jiggle;

    private double _scale = 1;
    private Vector _offset;
    private MapNode? _hover;
    private MapNode? _dragNode;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _isPanning;
    private bool _moved;

    static NetworkMapCanvas()
    {
        AffectsRender<NetworkMapCanvas>(NodesProperty, EdgesProperty, SelectedNodeProperty, RenderVersionProperty);
        FocusableProperty.OverrideDefaultValue<NetworkMapCanvas>(true);
    }

    public NetworkMapCanvas()
    {
        ClipToBounds = true;
        _jiggle = new JiggleSimulation<MapNode>(InvalidateVisual);
    }

    public IReadOnlyList<MapNode>? Nodes { get => GetValue(NodesProperty); set => SetValue(NodesProperty, value); }

    public IReadOnlyList<MapEdge>? Edges { get => GetValue(EdgesProperty); set => SetValue(EdgesProperty, value); }

    public MapNode? SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }

    public int RenderVersion { get => GetValue(RenderVersionProperty); set => SetValue(RenderVersionProperty, value); }

    public bool JiggleEnabled { get => GetValue(JiggleEnabledProperty); set => SetValue(JiggleEnabledProperty, value); }

    /// <summary>Raised when the user drops a node they dragged - the view model saves the layout.</summary>
    public event EventHandler<MapNode>? NodeMoved;

    /// <summary>Raised on double-clicking a node.</summary>
    public event EventHandler<MapNode>? NodeActivated;

    /// <summary>Zooms and pans so every node fits, with a margin; leaves small maps at no more than 1:1.</summary>
    public void FitToView()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (Nodes is not { Count: > 0 } nodes || width <= 0 || height <= 0)
        {
            return;
        }

        var left = nodes.Min(n => n.X);
        var right = nodes.Max(n => n.X);
        var top = nodes.Min(n => n.Y);
        var bottom = nodes.Max(n => n.Y);

        const double margin = 60;
        _scale = Math.Clamp(Math.Min((width - (margin * 2)) / Math.Max(right - left, 1), (height - (margin * 2)) / Math.Max(bottom - top, 1)), MinScale, 1);

        var centre = new Point((left + right) / 2, (top + bottom) / 2);
        _offset = new Vector((width / 2) - (centre.X * _scale), (height / 2) - (centre.Y * _scale));
        InvalidateVisual();
    }

    /// <summary>Pans (without changing zoom, unless zoomed right out) so this node is in the middle.</summary>
    public void CenterOn(MapNode node)
    {
        if (_scale < LabelAllScale)
        {
            _scale = 1;
        }

        _offset = new Vector((Bounds.Width / 2) - (node.X * _scale), (Bounds.Height / 2) - (node.Y * _scale));
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == NodesProperty)
        {
            _labels.Clear();
            _jiggle.Clear();
        }
        else if (change.Property == RenderVersionProperty)
        {
            _labels.Clear();
        }
        else if (change.Property == JiggleEnabledProperty)
        {
            _jiggle.IsEnabled = change.GetNewValue<bool>();
        }
        else if (change.Property == BoundsProperty
                 && change.GetOldValue<Rect>().Width == 0 && change.GetNewValue<Rect>().Width > 0)
        {
            // First real size after being hidden (the tab wasn't shown yet):
            // there was nothing to fit against until now.
            FitToView();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _jiggle.Clear();
    }

    public override void Render(DrawingContext context)
    {
        // A transparent fill so empty space takes the pointer, for panning.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

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

            // A link to a device that's down is dotted.
            var pen = edge.IsToOfflineDevice
                ? new Pen(highlighted ? accent : lineBrush, thickness, new DashStyle([1.0, 2.5], 0), PenLineCap.Round)
                : new Pen(highlighted ? accent : lineBrush, thickness);

            using (context.PushOpacity(highlighted ? 1 : dimOthers ? 0.15 : 0.5))
            {
                DrawEdgeLine(context, pen, edge);
            }
        }

        var radius = NodeRadius;
        var outline = new Pen(surface, 1.5);
        var selectedRing = new Pen(accent, 3);

        foreach (var node in nodes)
        {
            var centre = Shown(node);
            var faded = dimOthers && !ReferenceEquals(node, selected) && !neighbours.Contains(node);

            using (context.PushOpacity(faded ? 0.3 : 1))
            using (_jiggle.Stretch(node, centre, _scale) is { } stretch ? context.PushTransform(stretch) : default(DrawingContext.PushedState?))
            {
                var fill = Converters.StateBrush(node.State);
                if (node.IsNeighbour)
                {
                    // A neighbour isn't a LibreNMS device - smaller, and square.
                    var half = radius * 0.7;
                    context.DrawRectangle(fill, outline, new Rect(centre.X - half, centre.Y - half, half * 2, half * 2), 2, 2);
                    if (ReferenceEquals(node, selected))
                    {
                        context.DrawRectangle(null, selectedRing, new Rect(centre.X - half - 4, centre.Y - half - 4, (half + 4) * 2, (half + 4) * 2), 3, 3);
                    }
                }
                else
                {
                    context.DrawEllipse(fill, outline, centre, radius, radius);
                    if (ReferenceEquals(node, selected))
                    {
                        context.DrawEllipse(null, selectedRing, centre, radius + 4, radius + 4);
                    }
                }
            }
        }

        // Labels last, so no node is drawn over one.
        var labelAll = _scale >= LabelAllScale;
        foreach (var node in nodes)
        {
            var isFocus = ReferenceEquals(node, selected) || ReferenceEquals(node, _hover);
            if ((!labelAll && !isFocus && !neighbours.Contains(node)) || (dimOthers && !isFocus && !neighbours.Contains(node)))
            {
                continue;
            }

            DrawLabel(context, node, radius, isFocus, surface);
        }
    }

    private void DrawLabel(DrawingContext context, MapNode node, double radius, bool emphasise, IBrush surface)
    {
        if (!_labels.TryGetValue(node, out var text))
        {
            text = new FormattedText(
                node.Name,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(this.TryFindResource("BodyFont", ActualThemeVariant, out var font) && font is FontFamily family ? family : FontFamily.Default),
                11,
                Resource("TextPrimaryBrush", Brushes.White));
            _labels[node] = text;
        }

        var centre = Shown(node);
        var origin = new Point(centre.X - (text.Width / 2), centre.Y + radius + 3);

        if (emphasise)
        {
            // A backing plate so the hovered/selected label reads over lines.
            using (context.PushOpacity(0.85))
            {
                context.DrawRectangle(surface, null, new Rect(origin.X - 4, origin.Y - 1, text.Width + 8, text.Height + 2), 3, 3);
            }
        }

        context.DrawText(text, origin);
    }

    /// <summary>Node size tracks zoom, within limits - still findable zoomed out, not huge zoomed in.</summary>
    private double NodeRadius => Math.Clamp(8 * _scale, 3.5, 13);

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private Point ToScreen(MapNode node) => new((node.X * _scale) + _offset.X, (node.Y * _scale) + _offset.Y);

    /// <summary>The nodes linked to <paramref name="node"/>, which wobble a little in sympathy when it's dragged.</summary>
    private IEnumerable<MapNode> LinkedTo(MapNode node)
        => (Edges ?? Array.Empty<MapEdge>()).Where(e => e.Touches(node)).Select(e => ReferenceEquals(e.A, node) ? e.B : e.A);

    /// <summary>Where <paramref name="node"/> is drawn: its real position plus any jiggle still settling.</summary>
    private Point Shown(MapNode node) => ToScreen(node) + (_jiggle.Offset(node) * _scale);

    /// <summary>
    /// A link, straight - or, while either end is still jiggling, bowed back
    /// towards where its ends are coming from, like a rubber band dragged
    /// through jelly.
    /// </summary>
    private void DrawEdgeLine(DrawingContext context, IPen pen, MapEdge edge)
    {
        var a = Shown(edge.A);
        var b = Shown(edge.B);
        var bow = (_jiggle.Offset(edge.A) + _jiggle.Offset(edge.B)) * (_scale * 0.6);
        if (bow.SquaredLength < 0.25)
        {
            context.DrawLine(pen, a, b);
            return;
        }

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(a, isFilled: false);
            figure.QuadraticBezierTo(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2) + bow, b);
            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

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
            var distance = Point.Distance(Shown(node), screen);
            if (distance <= reach && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

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

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_dragNode is not null || _isPanning)
        {
            if (!_moved && Point.Distance(point, _pressPoint) < DragThreshold)
            {
                return;
            }

            _moved = true;

            if (_dragNode is not null)
            {
                var map = ToMap(point);
                var delta = new Vector(map.X - _dragNode.X, map.Y - _dragNode.Y);
                _dragNode.X = map.X;
                _dragNode.Y = map.Y;
                _jiggle.Moved(_dragNode, delta, LinkedTo(_dragNode));
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
            Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

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
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var point = e.GetPosition(this);
        var before = ToMap(point);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), MinScale, MaxScale);

        // Keep the point under the cursor fixed while zooming.
        _offset = new Vector(point.X - (before.X * _scale), point.Y - (before.Y * _scale));
        InvalidateVisual();
        e.Handled = true;
    }
}
