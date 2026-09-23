using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNMS.Converters;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// Draws the Geographical map: web map tiles (see <see cref="IMapTileService"/>)
/// with a pin per location on top, like LibreNMS's own Leaflet world map.
/// Wheel to zoom around the cursor, drag to pan, click a pin to select it,
/// double-click a merged pin to zoom into it. Pins that would overlap on
/// screen draw as one, with their device counts added together.
/// </summary>
public sealed class GeoMapCanvas : FrameworkElement
{
    public static readonly DependencyProperty PinsProperty = DependencyProperty.Register(
        nameof(Pins), typeof(IReadOnlyList<GeoPin>), typeof(GeoMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedPinsProperty = DependencyProperty.Register(
        nameof(SelectedPins), typeof(IReadOnlyList<GeoPin>), typeof(GeoMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TileTemplateProperty = DependencyProperty.Register(
        nameof(TileTemplate), typeof(string), typeof(GeoMapCanvas),
        new FrameworkPropertyMetadata(TileUrlTemplate.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles), typeof(IMapTileService), typeof(GeoMapCanvas),
        new FrameworkPropertyMetadata(null, OnTilesChanged));

    private const double MinZoom = 1;
    private const double MaxZoom = 18;
    private const int MaxTileZoom = 19;

    /// <summary>Pins closer together than this on screen are drawn as one - LibreNMS's own leaflet.group_radius idea.</summary>
    private const double ClusterRadius = 26;

    private const double DragThreshold = 3;

    private double _zoom = 2;
    private MapPoint _centre = new(128, 128);
    private List<List<GeoPin>> _clusters = new();
    private List<GeoPin>? _hover;
    private Point _pressPoint;
    private Point _lastPoint;
    private bool _isPanning;
    private bool _moved;

    public GeoMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<GeoPin>? Pins
    {
        get => (IReadOnlyList<GeoPin>?)GetValue(PinsProperty);
        set => SetValue(PinsProperty, value);
    }

    public IReadOnlyList<GeoPin>? SelectedPins
    {
        get => (IReadOnlyList<GeoPin>?)GetValue(SelectedPinsProperty);
        set => SetValue(SelectedPinsProperty, value);
    }

    public string TileTemplate
    {
        get => (string)GetValue(TileTemplateProperty);
        set => SetValue(TileTemplateProperty, value);
    }

    public IMapTileService? Tiles
    {
        get => (IMapTileService?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    private double Scale => Math.Pow(2, _zoom);

    /// <summary>
    /// The furthest out the map can go: the zoom at which the world (256
    /// world pixels, times 2^zoom) just covers the whole window in both
    /// directions - any further and the blank canvas behind it would show.
    /// </summary>
    private double MinZoomForView =>
        ActualWidth > 0 && ActualHeight > 0
            ? Math.Max(MinZoom, Math.Log2(Math.Max(ActualWidth, ActualHeight) / WebMercator.TileSize))
            : MinZoom;

    private static void OnTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (GeoMapCanvas)d;
        if (e.OldValue is IMapTileService old)
        {
            old.TileLoaded -= canvas.OnTileLoaded;
        }

        if (e.NewValue is IMapTileService tiles)
        {
            tiles.TileLoaded += canvas.OnTileLoaded;
        }
    }

    private void OnTileLoaded(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Zooms and pans so every pin is in view.</summary>
    public void FitToView()
    {
        if (Pins is not { Count: > 0 } pins || ActualWidth <= 0 || ActualHeight <= 0)
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
                (ActualWidth - margin * 2) / Math.Max(width, 1e-9),
                (ActualHeight - margin * 2) / Math.Max(height, 1e-9))), MinZoomForView, 16);

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

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width == 0 && sizeInfo.NewSize.Width > 0)
        {
            FitToView();
            return;
        }

        // A bigger window may now show past the world's edge at the current
        // zoom - zoom in / pan back just enough that it doesn't.
        ClampView();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Resource("SurfaceAltBrush", Brushes.DimGray), null, bounds);

        DrawTiles(dc);
        DrawPins(dc);
    }

    private void DrawTiles(DrawingContext dc)
    {
        if (Tiles is not { } tiles || ActualWidth <= 0)
        {
            return;
        }

        // Tiles from the zoom level nearest the current (fractional) zoom,
        // stretched by whatever's left over.
        var level = (int)Math.Clamp(Math.Round(_zoom), 0, MaxTileZoom);
        var tileCount = 1 << level;
        var tileSize = WebMercator.TileSize * Math.Pow(2, _zoom - level);

        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(ActualWidth, ActualHeight));
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
                    dc.DrawImage(image, rect);
                }
                else
                {
                    DrawFallbackTile(dc, tiles, template, level, x, y, rect);
                }
            }
        }
    }

    /// <summary>
    /// While a tile downloads, stand in a stretched piece of an already-loaded
    /// tile from a few levels up - so zooming in shows a blurry map that
    /// sharpens, rather than blank squares.
    /// </summary>
    private void DrawFallbackTile(DrawingContext dc, IMapTileService tiles, string template, int level, int x, int y, Rect rect)
    {
        for (var up = 1; up <= 4 && level - up >= 0; up++)
        {
            if (tiles.PeekTile(template, level - up, x >> up, y >> up) is not { } parent)
            {
                continue;
            }

            var span = 1 << up;
            var parentRect = new Rect(
                rect.X - (x % span) * (rect.Width - 0.5),
                rect.Y - (y % span) * (rect.Height - 0.5),
                (rect.Width - 0.5) * span + 0.5,
                (rect.Height - 0.5) * span + 0.5);

            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawImage(parent, parentRect);
            dc.Pop();
            return;
        }
    }

    private void DrawPins(DrawingContext dc)
    {
        if (Pins is not { Count: > 0 } pins)
        {
            _clusters = new List<List<GeoPin>>();
            return;
        }

        _clusters = PinClustering.Cluster(pins, p => ToMapPoint(ToScreen(p.World)), ClusterRadius);

        var selected = SelectedPins ?? Array.Empty<GeoPin>();
        var accent = Resource("AccentBrush", Brushes.DodgerBlue);
        var surface = Resource("SurfaceBrush", Brushes.Black);
        var text = Resource("TextPrimaryBrush", Brushes.White);
        var selectedRing = new Pen(accent, 3);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

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

            // Solid red only when everything that's being monitored there is
            // down - a genuine site outage. A partial outage keeps a neutral
            // pin and shows its share of red in the ring, plus a badge.
            var fullyDown = down > 0 && up == 0 && maintenance == 0;

            if (isSelected)
            {
                dc.DrawEllipse(null, selectedRing, centre, radius + 5, radius + 5);
            }

            dc.DrawEllipse(fullyDown ? SeverityToBrushConverter.Critical : surface, null, centre, radius, radius);

            if (!fullyDown)
            {
                DrawStateRing(dc, centre, radius, up, down, maintenance, inactive);
            }

            var countText = new FormattedText(
                count.ToString(CultureInfo.CurrentCulture),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                count >= 100 ? 10 : 11,
                fullyDown ? Brushes.White : text,
                dpi);
            dc.DrawText(countText, new Point(centre.X - countText.Width / 2, centre.Y - countText.Height / 2));

            if (down > 0 && !fullyDown)
            {
                DrawDownBadge(dc, centre, radius, down, dpi);
            }

            // Names only for the hovered/selected pin, or once zoomed in far
            // enough that labels won't collide.
            if (isSelected || ReferenceEquals(cluster, _hover) || _zoom >= 11)
            {
                DrawLabel(dc, cluster, centre, radius, dpi);
            }
        }
    }

    private void DrawLabel(DrawingContext dc, List<GeoPin> cluster, Point centre, double radius, double dpi)
    {
        var name = cluster.Count == 1 ? cluster[0].Name : $"{cluster.Count} locations";
        var text = new FormattedText(
            name,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Resource("TextPrimaryBrush", Brushes.White),
            dpi);

        var origin = new Point(centre.X - text.Width / 2, centre.Y + radius + 4);
        var plate = new Rect(origin.X - 5, origin.Y - 1, text.Width + 10, text.Height + 2);

        dc.PushOpacity(0.9);
        dc.DrawRoundedRectangle(Resource("SurfaceBrush", Brushes.Black), null, plate, 3, 3);
        dc.Pop();
        dc.DrawText(text, origin);
    }

    /// <summary>Bigger pins for bigger sites, within limits.</summary>
    private static double PinRadius(int deviceCount) => Math.Clamp(9 + Math.Log2(Math.Max(deviceCount, 1)) * 2, 10, 20);

    /// <summary>
    /// The pin's edge as a ring split by device state, each arc in proportion
    /// to how many devices there are in it - so a mostly-green ring with a
    /// thin red slice reads as "a couple down", not "site down". Starts at
    /// twelve o'clock with down first, so the red slice sits at the top
    /// right, beside the down-count badge.
    /// </summary>
    private static void DrawStateRing(DrawingContext dc, Point centre, double radius, int up, int down, int maintenance, int inactive)
    {
        var total = up + down + maintenance + inactive;
        if (total == 0)
        {
            return;
        }

        var thickness = Math.Max(3.5, radius * 0.32);
        var ringRadius = radius - thickness / 2;
        var start = -90.0;

        foreach (var (count, brush) in new[]
        {
            (down, (Brush)SeverityToBrushConverter.Critical),
            (maintenance, SeverityToBrushConverter.Maintenance),
            (inactive, SeverityToBrushConverter.Unknown),
            (up, SeverityToBrushConverter.Ok),
        })
        {
            if (count == 0)
            {
                continue;
            }

            var sweep = 360.0 * count / total;
            var pen = new Pen(brush, thickness);

            if (sweep >= 359.99)
            {
                dc.DrawEllipse(null, pen, centre, ringRadius, ringRadius);
            }
            else
            {
                dc.DrawGeometry(null, pen, Arc(centre, ringRadius, start, sweep));
            }

            start += sweep;
        }
    }

    private static StreamGeometry Arc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(radians), c.Y + r * Math.Sin(radians));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(On(centre, radius, startDegrees), isFilled: false, isClosed: false);
            context.ArcTo(
                On(centre, radius, startDegrees + sweepDegrees),
                new Size(radius, radius),
                0,
                isLargeArc: sweepDegrees > 180,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A small red count of devices down, on the pin's top-right edge.</summary>
    private static void DrawDownBadge(DrawingContext dc, Point centre, double radius, int down, double dpi)
    {
        var text = new FormattedText(
            down.ToString(CultureInfo.CurrentCulture),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            9,
            Brushes.White,
            dpi);

        var badgeRadius = Math.Max(7.5, text.Width / 2 + 3);
        var offset = radius * 0.72;
        var badgeCentre = new Point(centre.X + offset, centre.Y - offset);

        dc.DrawEllipse(SeverityToBrushConverter.Critical, new Pen(Brushes.White, 1.5), badgeCentre, badgeRadius, badgeRadius);
        dc.DrawText(text, new Point(badgeCentre.X - text.Width / 2, badgeCentre.Y - text.Height / 2));
    }

    private Point ClusterCentre(List<GeoPin> cluster) => new(
        cluster.Average(p => ToScreen(p.World).X),
        cluster.Average(p => ToScreen(p.World).Y));

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private Point ToScreen(MapPoint world) => new(
        (world.X - _centre.X) * Scale + ActualWidth / 2,
        (world.Y - _centre.Y) * Scale + ActualHeight / 2);

    private MapPoint ToWorld(Point screen) => new(
        (screen.X - ActualWidth / 2) / Scale + _centre.X,
        (screen.Y - ActualHeight / 2) / Scale + _centre.Y);

    private static MapPoint ToMapPoint(Point p) => new(p.X, p.Y);

    private List<GeoPin>? HitTest(Point screen)
    {
        foreach (var cluster in _clusters)
        {
            var radius = PinRadius(cluster.Sum(p => p.DeviceCount)) + 3;
            if ((ClusterCentre(cluster) - screen).Length <= radius)
            {
                return cluster;
            }
        }

        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
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
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (_isPanning)
        {
            if (!_moved && (point - _pressPoint).Length < DragThreshold)
            {
                return;
            }

            _moved = true;
            var delta = point - _lastPoint;
            _centre = new MapPoint(_centre.X - delta.X / Scale, _centre.Y - delta.Y / Scale);
            ClampView();
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

        if (_isPanning && !_moved)
        {
            SelectedPins = Array.Empty<GeoPin>();
        }

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
        var before = ToWorld(point);
        _zoom = Math.Clamp(_zoom + e.Delta / 120.0 * 0.5, MinZoomForView, MaxZoom);

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
    /// of the world that half a window's width/height still fits inside it.
    /// </summary>
    private void ClampView()
    {
        _zoom = Math.Clamp(_zoom, MinZoomForView, MaxZoom);

        var halfWidth = ActualWidth / 2 / Scale;
        var halfHeight = ActualHeight / 2 / Scale;
        var size = (double)WebMercator.TileSize;

        _centre = new MapPoint(
            halfWidth * 2 >= size ? size / 2 : Math.Clamp(_centre.X, halfWidth, size - halfWidth),
            halfHeight * 2 >= size ? size / 2 : Math.Clamp(_centre.Y, halfHeight, size - halfHeight));
    }
}
