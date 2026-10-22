using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// Draws the Geographical map (#250, from the WPF app's GeoMapCanvas): web
/// map tiles (see <see cref="IMapTileService"/>) with a pin per location on
/// top, like LibreNMS's own Leaflet world map. Wheel to zoom around the
/// cursor, drag to pan, click a pin to select it, double-click a merged pin to
/// zoom into it. Each pin is a donut of its devices' states around the device
/// count. Pins that would overlap on screen draw as one, their counts added.
/// </summary>
public sealed class GeoMapCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<GeoPin>?> PinsProperty =
        AvaloniaProperty.Register<GeoMapCanvas, IReadOnlyList<GeoPin>?>(nameof(Pins));

    public static readonly StyledProperty<IReadOnlyList<GeoPin>?> SelectedPinsProperty =
        AvaloniaProperty.Register<GeoMapCanvas, IReadOnlyList<GeoPin>?>(nameof(SelectedPins), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> TileTemplateProperty =
        AvaloniaProperty.Register<GeoMapCanvas, string>(nameof(TileTemplate), TileUrlTemplate.Default);

    public static readonly StyledProperty<IMapTiles?> TilesProperty =
        AvaloniaProperty.Register<GeoMapCanvas, IMapTiles?>(nameof(Tiles));

    private const double MinZoom = 1;
    private const double MaxZoom = 18;
    private const int MaxTileZoom = 19;

    /// <summary>Pins closer together than this on screen are drawn as one - LibreNMS's own leaflet.group_radius idea.</summary>
    private const double ClusterRadius = 26;

    private const double DragThreshold = 3;

    private double _zoom = 2;
    private MapPoint _centre = new(128, 128);
    private List<List<GeoPin>> _clusters = [];
    private List<GeoPin>? _hover;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _isPanning;
    private bool _moved;

    static GeoMapCanvas()
    {
        AffectsRender<GeoMapCanvas>(PinsProperty, SelectedPinsProperty, TileTemplateProperty);
        FocusableProperty.OverrideDefaultValue<GeoMapCanvas>(true);
    }

    public GeoMapCanvas()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<GeoPin>? Pins { get => GetValue(PinsProperty); set => SetValue(PinsProperty, value); }

    public IReadOnlyList<GeoPin>? SelectedPins { get => GetValue(SelectedPinsProperty); set => SetValue(SelectedPinsProperty, value); }

    public string TileTemplate { get => GetValue(TileTemplateProperty); set => SetValue(TileTemplateProperty, value); }

    public IMapTiles? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    private double Scale => Math.Pow(2, _zoom);

    /// <summary>
    /// The furthest out the map can go: the zoom at which the world (256
    /// world pixels, times 2^zoom) just covers the whole window in both
    /// directions - any further and the blank canvas behind it would show.
    /// </summary>
    private double MinZoomForView =>
        Bounds.Width > 0 && Bounds.Height > 0
            ? Math.Max(MinZoom, Math.Log2(Math.Max(Bounds.Width, Bounds.Height) / WebMercator.TileSize))
            : MinZoom;

    /// <summary>Zooms and pans so every pin is in view.</summary>
    public void FitToView()
    {
        if (Pins is not { Count: > 0 } pins || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var left = pins.Min(p => p.World.X);
        var right = pins.Max(p => p.World.X);
        var top = pins.Min(p => p.World.Y);
        var bottom = pins.Max(p => p.World.Y);
        _centre = new MapPoint((left + right) / 2, (top + bottom) / 2);

        const double margin = 80;
        var width = right - left;
        var height = bottom - top;

        // A single site (or several at one spot) gets a street-level view;
        // otherwise the tightest zoom that fits them all.
        _zoom = width < 1e-9 && height < 1e-9
            ? 14
            : Math.Clamp(Math.Log2(Math.Min(
                (Bounds.Width - (margin * 2)) / Math.Max(width, 1e-9),
                (Bounds.Height - (margin * 2)) / Math.Max(height, 1e-9))), MinZoomForView, 16);

        ClampView();
        InvalidateVisual();
    }

    public void CenterOn(GeoPin pin)
    {
        _centre = pin.World;
        _zoom = Math.Max(_zoom, 12);
        ClampView();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TilesProperty)
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
        else if (change.Property == BoundsProperty)
        {
            if (change.GetOldValue<Rect>().Width == 0 && change.GetNewValue<Rect>().Width > 0)
            {
                FitToView();
                return;
            }

            // A bigger window may now show past the world's edge at the current
            // zoom - zoom in or pan back just enough that it doesn't.
            ClampView();
        }
    }

    private void OnTileLoaded(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Resource("SurfaceAltBrush", Brushes.DimGray), new Rect(Bounds.Size));
        DrawTiles(context);
        DrawPins(context);
    }

    private void DrawTiles(DrawingContext context)
    {
        if (Tiles is not IMapTileService tiles || Bounds.Width <= 0)
        {
            return;
        }

        // Tiles from the zoom level nearest the current (fractional) zoom,
        // stretched by whatever's left over.
        var level = (int)Math.Clamp(Math.Round(_zoom), 0, MaxTileZoom);
        var tileCount = 1 << level;
        var tileSize = WebMercator.TileSize * Math.Pow(2, _zoom - level);

        var topLeft = ToWorld(default);
        var bottomRight = ToWorld(new Point(Bounds.Width, Bounds.Height));
        var toTile = tileCount / (double)WebMercator.TileSize;

        var x0 = Math.Max(0, (int)Math.Floor(topLeft.X * toTile));
        var x1 = Math.Min(tileCount - 1, (int)Math.Floor(bottomRight.X * toTile));
        var y0 = Math.Max(0, (int)Math.Floor(topLeft.Y * toTile));
        var y1 = Math.Min(tileCount - 1, (int)Math.Floor(bottomRight.Y * toTile));

        var template = TileTemplate;

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var origin = ToScreen(new MapPoint(x / toTile, y / toTile));
                var rect = new Rect(origin.X, origin.Y, tileSize + 0.5, tileSize + 0.5);

                if (tiles.GetTile(template, level, x, y) is { } image)
                {
                    context.DrawImage(image, rect);
                }
                else
                {
                    DrawFallbackTile(context, tiles, template, level, x, y, rect);
                }
            }
        }
    }

    /// <summary>
    /// While a tile downloads, stand in a stretched piece of an already-loaded
    /// tile from a few levels up - so zooming in shows a blurry map that
    /// sharpens, rather than blank squares.
    /// </summary>
    private static void DrawFallbackTile(DrawingContext context, IMapTileService tiles, string template, int level, int x, int y, Rect rect)
    {
        for (var up = 1; up <= 4 && level - up >= 0; up++)
        {
            if (tiles.PeekTile(template, level - up, x >> up, y >> up) is not { } parent)
            {
                continue;
            }

            var span = 1 << up;
            var parentRect = new Rect(
                rect.X - ((x % span) * (rect.Width - 0.5)),
                rect.Y - ((y % span) * (rect.Height - 0.5)),
                ((rect.Width - 0.5) * span) + 0.5,
                ((rect.Height - 0.5) * span) + 0.5);

            using (context.PushClip(rect))
            {
                context.DrawImage(parent, parentRect);
            }

            return;
        }
    }

    private void DrawPins(DrawingContext context)
    {
        if (Pins is not { Count: > 0 } pins)
        {
            _clusters = [];
            return;
        }

        _clusters = PinClustering.Cluster(pins, p => ToMapPoint(ToScreen(p.World)), ClusterRadius);

        var selected = SelectedPins ?? Array.Empty<GeoPin>();
        var surface = Resource("SurfaceBrush", Brushes.Black);
        var text = Resource("TextPrimaryBrush", Brushes.White);
        var selectedRing = new Pen(Resource("AccentBrush", Brushes.DodgerBlue), 3);

        foreach (var cluster in _clusters)
        {
            var centre = ClusterCentre(cluster);
            var up = cluster.Sum(p => p.UpCount);
            var down = cluster.Sum(p => p.DownCount);
            var maintenance = cluster.Sum(p => p.MaintenanceCount);
            var inactive = cluster.Sum(p => p.InactiveCount);
            var count = up + down + maintenance + inactive;
            var radius = PinRadius(count);
            var isSelected = cluster.Any(selected.Contains);

            if (isSelected)
            {
                context.DrawEllipse(null, selectedRing, centre, radius + 5, radius + 5);
            }

            // A donut: the ring is the mix of device states, the hole holds
            // the device count. An impression of the site's health, not a
            // precise figure - the details panel has the exact numbers.
            context.DrawEllipse(surface, null, centre, radius, radius);
            DrawStateRing(context, centre, radius, up, down, maintenance, inactive);

            var countText = new FormattedText(
                count.ToString(CultureInfo.CurrentCulture),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(BodyFont, FontStyle.Normal, FontWeight.SemiBold),
                count >= 100 ? 9 : 10,
                text);
            context.DrawText(countText, new Point(centre.X - (countText.Width / 2), centre.Y - (countText.Height / 2)));

            // Names only for the hovered or selected pin, or once zoomed in far
            // enough that labels won't collide.
            if (isSelected || ReferenceEquals(cluster, _hover) || _zoom >= 11)
            {
                DrawLabel(context, cluster, centre, radius);
            }
        }
    }

    private void DrawLabel(DrawingContext context, List<GeoPin> cluster, Point centre, double radius)
    {
        var name = cluster.Count == 1 ? cluster[0].Name : $"{cluster.Count} locations";
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(BodyFont), 11, Resource("TextPrimaryBrush", Brushes.White));

        var origin = new Point(centre.X - (text.Width / 2), centre.Y + radius + 4);
        using (context.PushOpacity(0.9))
        {
            context.DrawRectangle(Resource("SurfaceBrush", Brushes.Black), null, new Rect(origin.X - 5, origin.Y - 1, text.Width + 10, text.Height + 2), 3, 3);
        }

        context.DrawText(text, origin);
    }

    /// <summary>Bigger pins for bigger sites, within limits.</summary>
    private static double PinRadius(int deviceCount) => Math.Clamp(9 + (Math.Log2(Math.Max(deviceCount, 1)) * 2), 10, 20);

    /// <summary>
    /// The donut's ring, split by device state in proportion to how many
    /// devices are in each - a mostly-green ring with a thin red slice reads
    /// as "a couple down", not "site down". Starts at twelve o'clock, down
    /// first.
    /// </summary>
    private static void DrawStateRing(DrawingContext context, Point centre, double radius, int up, int down, int maintenance, int inactive)
    {
        var total = up + down + maintenance + inactive;
        if (total == 0)
        {
            return;
        }

        var thickness = Math.Max(4, radius * 0.4);
        var ringRadius = radius - (thickness / 2);
        var start = -90.0;

        foreach (var (count, state) in new[] { (down, DeviceState.Down), (maintenance, DeviceState.Maintenance), (inactive, DeviceState.Disabled), (up, DeviceState.Up) })
        {
            if (count == 0)
            {
                continue;
            }

            var sweep = 360.0 * count / total;
            var pen = new Pen(Converters.StateBrush(state), thickness);

            if (sweep >= 359.99)
            {
                context.DrawEllipse(null, pen, centre, ringRadius, ringRadius);
            }
            else
            {
                context.DrawGeometry(null, pen, Arc(centre, ringRadius, start, sweep));
            }

            start += sweep;
        }
    }

    private static StreamGeometry Arc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(c.X + (r * Math.Cos(radians)), c.Y + (r * Math.Sin(radians)));
        }

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(On(centre, radius, startDegrees), isFilled: false);
            figure.ArcTo(On(centre, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0, isLargeArc: sweepDegrees > 180, SweepDirection.Clockwise);
            figure.EndFigure(isClosed: false);
        }

        return geometry;
    }

    private FontFamily BodyFont => this.TryFindResource("BodyFont", ActualThemeVariant, out var font) && font is FontFamily family ? family : FontFamily.Default;

    private Point ClusterCentre(List<GeoPin> cluster) => new(
        cluster.Average(p => ToScreen(p.World).X),
        cluster.Average(p => ToScreen(p.World).Y));

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private Point ToScreen(MapPoint world) => new(
        ((world.X - _centre.X) * Scale) + (Bounds.Width / 2),
        ((world.Y - _centre.Y) * Scale) + (Bounds.Height / 2));

    private MapPoint ToWorld(Point screen) => new(
        ((screen.X - (Bounds.Width / 2)) / Scale) + _centre.X,
        ((screen.Y - (Bounds.Height / 2)) / Scale) + _centre.Y);

    private static MapPoint ToMapPoint(Point p) => new(p.X, p.Y);

    private List<GeoPin>? HitTest(Point screen)
    {
        foreach (var cluster in _clusters)
        {
            var radius = PinRadius(cluster.Sum(p => p.DeviceCount)) + 3;
            if (Point.Distance(ClusterCentre(cluster), screen) <= radius)
            {
                return cluster;
            }
        }

        return null;
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

        if (e.ClickCount == 2 && hit is { Count: > 1 })
        {
            // A merged pin: zoom in on it until its locations separate.
            _centre = ToWorld(ClusterCentre(hit));
            _zoom = Math.Min(MaxZoom, _zoom + 2);
            ClampView();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (hit is not null)
        {
            SelectedPins = hit;
            e.Handled = true;
            return;
        }

        _pressPoint = point;
        _lastPoint = point;
        _moved = false;
        _isPanning = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);

        if (_isPanning)
        {
            if (!_moved && Point.Distance(point, _pressPoint) < DragThreshold)
            {
                return;
            }

            _moved = true;
            _centre = new MapPoint(_centre.X - ((point.X - _lastPoint.X) / Scale), _centre.Y - ((point.Y - _lastPoint.Y) / Scale));
            ClampView();
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

        if (_isPanning && !_moved)
        {
            SelectedPins = Array.Empty<GeoPin>();
        }

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
        var before = ToWorld(point);
        _zoom = Math.Clamp(_zoom + (e.Delta.Y * 0.5), MinZoomForView, MaxZoom);

        // Keep the point under the cursor fixed while zooming.
        var after = ToWorld(point);
        _centre = new MapPoint(_centre.X + before.X - after.X, _centre.Y + before.Y - after.Y);
        ClampView();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Keeps the window entirely over the map: zoom no further out than
    /// <see cref="MinZoomForView"/>, and the centre far enough from each edge
    /// of the world that half a window's width and height still fit inside it.
    /// </summary>
    private void ClampView()
    {
        _zoom = Math.Clamp(_zoom, MinZoomForView, MaxZoom);

        var halfWidth = Bounds.Width / 2 / Scale;
        var halfHeight = Bounds.Height / 2 / Scale;
        var size = (double)WebMercator.TileSize;

        _centre = new MapPoint(
            halfWidth * 2 >= size ? size / 2 : Math.Clamp(_centre.X, halfWidth, size - halfWidth),
            halfHeight * 2 >= size ? size / 2 : Math.Clamp(_centre.Y, halfHeight, size - halfHeight));
    }
}
