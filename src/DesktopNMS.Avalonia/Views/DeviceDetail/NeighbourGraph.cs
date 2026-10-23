using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// The Neighbours section's graph (#250, from the WPF app's NeighbourGraph):
/// this device in the middle, what it's connected to around it, a line per
/// connection - green while this device's port is up, red while it's down,
/// dashed once LibreNMS stops seeing it. Hovering a neighbour (here or its
/// card) shows both ends of the cable along its line; clicking a LibreNMS
/// device moves the window to it, as its card does.
/// </summary>
public sealed class NeighbourGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<DeviceNeighbourItemViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<NeighbourGraph, IReadOnlyList<DeviceNeighbourItemViewModel>?>(nameof(Items));

    public static readonly StyledProperty<string?> CenterNameProperty =
        AvaloniaProperty.Register<NeighbourGraph, string?>(nameof(CenterName));

    public static readonly StyledProperty<AlertSeverity> CenterSeverityProperty =
        AvaloniaProperty.Register<NeighbourGraph, AlertSeverity>(nameof(CenterSeverity), AlertSeverity.Unknown);

    private const double NodeRadius = 15;
    private const double CenterRadius = 24;

    private readonly List<(DeviceNeighbourItemViewModel Item, Point At)> _placed = new();
    private DeviceNeighbourItemViewModel? _hovered;

    static NeighbourGraph()
    {
        AffectsRender<NeighbourGraph>(ItemsProperty, CenterNameProperty, CenterSeverityProperty);
        ItemsProperty.Changed.AddClassHandler<NeighbourGraph>((graph, e) => graph.OnItemsChanged(e));
    }

    public NeighbourGraph()
    {
        Background = Brushes.Transparent;
    }

    /// <summary>Hit-testing needs a fill; transparent, so the card shows through.</summary>
    public IBrush? Background { get; set; }

    public IReadOnlyList<DeviceNeighbourItemViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? CenterName
    {
        get => GetValue(CenterNameProperty);
        set => SetValue(CenterNameProperty, value);
    }

    public AlertSeverity CenterSeverity
    {
        get => GetValue(CenterSeverityProperty);
        set => SetValue(CenterSeverityProperty, value);
    }

    // A card's hover highlights its node too - redraw when one changes.
    private void OnItemsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is IEnumerable<DeviceNeighbourItemViewModel> old)
        {
            foreach (var item in old)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        if (e.NewValue is IEnumerable<DeviceNeighbourItemViewModel> items)
        {
            foreach (var item in items)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceNeighbourItemViewModel.IsHighlighted) or nameof(DeviceNeighbourItemViewModel.RemotePortText))
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        context.FillRectangle(Background ?? Brushes.Transparent, new Rect(0, 0, width, height));
        _placed.Clear();

        var surface = Find("SurfaceBrush", Brushes.Black);
        var line = Find("BorderBrush", Brushes.DimGray);
        var text = Find("TextPrimaryBrush", Brushes.White);
        var subtle = Find("TextSecondaryBrush", Brushes.Gray);
        var accent = Find("AccentBrush", Brushes.DodgerBlue);
        var ok = Find("OkBrush", Brushes.SeaGreen);
        var warning = Find("WarningBrush", Brushes.Goldenrod);
        var critical = Find("CriticalBrush", Brushes.IndianRed);
        var font = this.TryFindResource("BodyFont", ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default;

        var center = new Point(width / 2, height / 2);
        var items = Items ?? [];

        // One ring, or two alternating rings once there are too many to label around one.
        var outer = Math.Max(60, (Math.Min(width, height) / 2) - 48);
        var twoRings = items.Count > 14;
        for (var i = 0; i < items.Count; i++)
        {
            var angle = (2 * Math.PI * i / items.Count) - (Math.PI / 2);
            var radius = twoRings && i % 2 == 1 ? outer * 0.62 : outer;
            _placed.Add((items[i], new Point(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)))));
        }

        // Lines first, so nodes sit on top of them.
        foreach (var (item, at) in _placed)
        {
            var highlighted = item.IsHighlighted;
            var brush = highlighted ? accent : item.LocalPort is { } port ? (port.IsUp ? ok : critical) : line;
            var pen = new Pen(brush, highlighted ? 2.5 : 1.4, item.IsStale ? DashStyle.Dash : null);
            using (context.PushOpacity(item.IsStale ? 0.45 : highlighted ? 1 : 0.7))
            {
                context.DrawLine(pen, center, at);
            }
        }

        // This device.
        context.DrawEllipse(surface, new Pen(accent, 3), center, CenterRadius, CenterRadius);
        context.DrawEllipse(SeverityBrush(CenterSeverity), null, center, 6, 6);
        DrawLabel(context, CenterName ?? string.Empty, center.X, center.Y + CenterRadius + 6, 180, 13, FontWeight.SemiBold, text, font);

        foreach (var (item, at) in _placed)
        {
            var stateBrush = SeverityBrush(item.StateSeverity);
            var ring = new Pen(item.IsHighlighted ? accent : stateBrush, item.IsHighlighted ? 3 : 2, item.IsKnownDevice ? null : DashStyle.Dot);

            using (context.PushOpacity(item.IsStale && item.IsKnownDevice ? 0.55 : 1))
            {
                context.DrawEllipse(surface, ring, at, NodeRadius, NodeRadius);
                context.DrawEllipse(stateBrush, null, at, 4.5, 4.5);
                DrawLabel(context, item.Name, at.X, at.Y + NodeRadius + 4, 140, 12, item.IsHighlighted ? FontWeight.SemiBold : FontWeight.Normal, text, font);
            }

            // The cable's two ends, along its line, while it's highlighted.
            if (item.IsHighlighted)
            {
                DrawPill(context, item.LocalPortText, Lerp(center, at, 0.3), surface, accent, text, font);
                DrawPill(context, item.RemotePortText, Lerp(center, at, 0.72), surface, accent, text, font);
            }
        }

        if (items.Count == 0)
        {
            DrawLabel(context, "No neighbours to show", center.X, center.Y + CenterRadius + 26, 240, 12, FontWeight.Normal, subtle, font);
        }

        IBrush SeverityBrush(AlertSeverity severity) => severity switch
        {
            AlertSeverity.Ok => ok,
            AlertSeverity.Critical => critical,
            AlertSeverity.Warning => warning,
            _ => subtle,
        };
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static FormattedText Text(string value, double size, FontWeight weight, IBrush brush, FontFamily font, double maxWidth) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private static void DrawLabel(DrawingContext context, string value, double centerX, double top, double maxWidth, double size, FontWeight weight, IBrush brush, FontFamily font)
    {
        var formatted = Text(value, size, weight, brush, font, maxWidth);
        formatted.TextAlignment = TextAlignment.Center;
        context.DrawText(formatted, new Point(centerX - (maxWidth / 2), top));
    }

    private static void DrawPill(DrawingContext context, string value, Point at, IBrush fill, IBrush border, IBrush brush, FontFamily font)
    {
        var formatted = Text(value, 11, FontWeight.Normal, brush, font, 160);
        var box = new Rect(at.X - (formatted.Width / 2) - 7, at.Y - (formatted.Height / 2) - 3, formatted.Width + 14, formatted.Height + 6);
        context.DrawRectangle(fill, new Pen(border, 1), box, 9, 9);
        context.DrawText(formatted, new Point(box.X + 7, box.Y + 3));
    }

    private IBrush Find(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private DeviceNeighbourItemViewModel? HitTest(Point position) => _placed
        .Where(p => Point.Distance(p.At, position) <= NodeRadius + 5)
        .Select(p => p.Item)
        .FirstOrDefault();

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hovered))
        {
            return;
        }

        if (_hovered is not null)
        {
            _hovered.IsHighlighted = false;
        }

        _hovered = hit;
        if (hit is not null)
        {
            hit.IsHighlighted = true;
        }

        Cursor = hit?.IsKnownDevice == true ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetTip(this, hit is null ? null : $"{hit.Name}\n{hit.ConnectionText}\n{hit.SourceText}");
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is not null)
        {
            _hovered.IsHighlighted = false;
            _hovered = null;
        }

        Cursor = null;
        ToolTip.SetTip(this, null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left
            && HitTest(e.GetPosition(this)) is { IsKnownDevice: true } item && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }
}
