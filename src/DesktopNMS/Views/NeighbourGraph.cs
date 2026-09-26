using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Neighbours tab's graph: this device in the middle, what it's connected
/// to around it, a line per connection - green while this device's port is
/// up, red while it's down, dashed once LibreNMS stops seeing it. Hovering a
/// neighbour (here or its card) shows both ends of the cable along its line;
/// clicking a LibreNMS device moves the window to it, as its card does.
/// Drawn in OnRender, like the other maps.
/// </summary>
public sealed class NeighbourGraph : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IReadOnlyList<DeviceNeighbourItemViewModel>),
        typeof(NeighbourGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty CenterNameProperty = DependencyProperty.Register(
        nameof(CenterName),
        typeof(string),
        typeof(NeighbourGraph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterSeverityProperty = DependencyProperty.Register(
        nameof(CenterSeverity),
        typeof(AlertSeverity),
        typeof(NeighbourGraph),
        new FrameworkPropertyMetadata(AlertSeverity.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double NodeRadius = 15;
    private const double CenterRadius = 24;

    private readonly List<(DeviceNeighbourItemViewModel Item, Point At)> _placed = new();
    private DeviceNeighbourItemViewModel? _hovered;

    public IReadOnlyList<DeviceNeighbourItemViewModel>? Items
    {
        get => (IReadOnlyList<DeviceNeighbourItemViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string CenterName
    {
        get => (string)GetValue(CenterNameProperty);
        set => SetValue(CenterNameProperty, value);
    }

    public AlertSeverity CenterSeverity
    {
        get => (AlertSeverity)GetValue(CenterSeverityProperty);
        set => SetValue(CenterSeverityProperty, value);
    }

    // A card's hover highlights its node too - redraw when one changes.
    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (NeighbourGraph)d;
        if (e.OldValue is IEnumerable<DeviceNeighbourItemViewModel> old)
        {
            foreach (var item in old)
            {
                item.PropertyChanged -= graph.OnItemPropertyChanged;
            }
        }

        if (e.NewValue is IEnumerable<DeviceNeighbourItemViewModel> items)
        {
            foreach (var item in items)
            {
                item.PropertyChanged += graph.OnItemPropertyChanged;
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

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        _placed.Clear();

        var surface = Find("SurfaceBrush", Brushes.Black);
        var line = Find("BorderBrush", Brushes.DimGray);
        var text = Find("TextPrimaryBrush", Brushes.White);
        var subtle = Find("TextSecondaryBrush", Brushes.Gray);
        var accent = Find("AccentBrush", Brushes.DodgerBlue);
        var ok = Find("OkBrush", Brushes.SeaGreen);
        var critical = Find("CriticalBrush", Brushes.IndianRed);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var center = new Point(width / 2, height / 2);
        var items = Items ?? Array.Empty<DeviceNeighbourItemViewModel>();

        // One ring, or two alternating rings once there are too many to
        // label around one.
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
            var pen = new Pen(brush, highlighted ? 2.5 : 1.4);
            if (item.IsStale)
            {
                pen.DashStyle = DashStyles.Dash;
            }

            dc.PushOpacity(item.IsStale ? 0.45 : highlighted ? 1 : 0.7);
            dc.DrawLine(pen, center, at);
            dc.Pop();
        }

        // This device.
        dc.DrawEllipse(surface, new Pen(accent, 3), center, CenterRadius, CenterRadius);
        dc.DrawEllipse(SeverityBrush(CenterSeverity, ok, critical, subtle), null, center, 6, 6);
        DrawLabel(dc, CenterName, center.X, center.Y + CenterRadius + 6, 180, 13, FontWeights.SemiBold, text, dpi);

        foreach (var (item, at) in _placed)
        {
            var stateBrush = SeverityBrush(item.StateSeverity, ok, critical, subtle);
            var ring = new Pen(item.IsHighlighted ? accent : stateBrush, item.IsHighlighted ? 3 : 2);
            if (!item.IsKnownDevice)
            {
                ring.DashStyle = DashStyles.Dot;
            }

            dc.PushOpacity(item.IsStale ? 0.55 : 1);
            dc.DrawEllipse(surface, ring, at, NodeRadius, NodeRadius);
            dc.DrawEllipse(stateBrush, null, at, 4.5, 4.5);
            DrawLabel(dc, item.Name, at.X, at.Y + NodeRadius + 4, 140, 12, item.IsHighlighted ? FontWeights.SemiBold : FontWeights.Normal, text, dpi);
            dc.Pop();

            // The cable's two ends, along its line, while it's highlighted.
            if (item.IsHighlighted)
            {
                DrawPill(dc, item.LocalPortText, Lerp(center, at, 0.3), surface, accent, text, dpi);
                DrawPill(dc, item.RemotePortText, Lerp(center, at, 0.72), surface, accent, text, dpi);
            }
        }

        if (items.Count == 0)
        {
            DrawLabel(dc, "No neighbours to show", center.X, center.Y + CenterRadius + 26, 240, 12, FontWeights.Normal, subtle, dpi);
        }
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static Brush SeverityBrush(AlertSeverity severity, Brush ok, Brush critical, Brush unknown) => severity switch
    {
        AlertSeverity.Ok => ok,
        AlertSeverity.Critical => critical,
        AlertSeverity.Warning => Converters.SeverityToBrushConverter.Warning,
        _ => unknown,
    };

    private static void DrawLabel(DrawingContext dc, string value, double centerX, double top, double maxWidth, double size, FontWeight weight, Brush brush, double dpi)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(formatted, new Point(centerX - (maxWidth / 2), top));
    }

    private static void DrawPill(DrawingContext dc, string value, Point at, Brush fill, Brush border, Brush brush, double dpi)
    {
        var formatted = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, dpi)
        {
            MaxTextWidth = 160,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var box = new Rect(at.X - (formatted.Width / 2) - 7, at.Y - (formatted.Height / 2) - 3, formatted.Width + 14, formatted.Height + 6);
        dc.DrawRoundedRectangle(fill, new Pen(border, 1), box, 9, 9);
        dc.DrawText(formatted, new Point(box.X + 7, box.Y + 3));
    }

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private DeviceNeighbourItemViewModel? HitTest(Point position) => _placed
        .Where(p => (p.At - position).Length <= NodeRadius + 5)
        .Select(p => p.Item)
        .FirstOrDefault();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
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

        Cursor = hit?.IsKnownDevice == true ? Cursors.Hand : null;
        ToolTip = hit is null ? null : $"{hit.Name}\n{hit.ConnectionText}\n{hit.SourceText}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is not null)
        {
            _hovered.IsHighlighted = false;
            _hovered = null;
        }

        Cursor = null;
        ToolTip = null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { IsKnownDevice: true } item && item.OpenCommand.CanExecute(null))
        {
            item.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }
}
