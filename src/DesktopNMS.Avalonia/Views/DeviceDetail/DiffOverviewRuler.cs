using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// A thin strip beside the Unimus diff viewer (#115, from the WPF app's
/// DiffOverviewRuler) showing where every change sits in the whole diff - red
/// for removed lines, green for added - and a box for the part in view, like
/// an editor's scrollbar markers. Click or drag on it to jump there. Drawn
/// directly: a config diff can be thousands of rows long.
/// </summary>
public sealed class DiffOverviewRuler : Control
{
    public static readonly StyledProperty<IReadOnlyList<UnimusDiffLineViewModel>?> LinesProperty =
        AvaloniaProperty.Register<DiffOverviewRuler, IReadOnlyList<UnimusDiffLineViewModel>?>(nameof(Lines));

    public static readonly StyledProperty<double> ViewportStartProperty =
        AvaloniaProperty.Register<DiffOverviewRuler, double>(nameof(ViewportStart));

    public static readonly StyledProperty<double> ViewportSizeProperty =
        AvaloniaProperty.Register<DiffOverviewRuler, double>(nameof(ViewportSize));

    private bool _dragging;

    static DiffOverviewRuler()
    {
        AffectsRender<DiffOverviewRuler>(LinesProperty, ViewportStartProperty, ViewportSizeProperty);
    }

    public DiffOverviewRuler()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public IReadOnlyList<UnimusDiffLineViewModel>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>Top of the part in view, as a fraction (0-1) of the whole diff.</summary>
    public double ViewportStart
    {
        get => GetValue(ViewportStartProperty);
        set => SetValue(ViewportStartProperty, value);
    }

    /// <summary>Height of the part in view, as a fraction (0-1) of the whole diff.</summary>
    public double ViewportSize
    {
        get => GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    /// <summary>Raised with the clicked position as a fraction (0-1) of the whole diff.</summary>
    public event EventHandler<double>? NavigateRequested;

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;
        var width = Bounds.Width;

        // A transparent fill so the whole strip is hit-testable, not just the markers.
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, width, height));

        if (Lines is not { Count: > 0 } lines || height <= 0)
        {
            return;
        }

        var removed = Find("CriticalBrush", Brushes.IndianRed);
        var added = Find("OkBrush", Brushes.SeaGreen);
        var hidden = Find("TextSecondaryBrush", Brushes.Gray);
        var rowHeight = height / lines.Count;
        var markerHeight = Math.Max(2, rowHeight);

        // Consecutive rows of the same kind are merged into one rectangle: far fewer draws on a long change.
        var i = 0;
        while (i < lines.Count)
        {
            var brush = BrushFor(lines[i]);
            var start = i;
            while (i < lines.Count && ReferenceEquals(BrushFor(lines[i]), brush))
            {
                i++;
            }

            if (brush is null)
            {
                continue;
            }

            var top = start * rowHeight;
            if (ReferenceEquals(brush, hidden))
            {
                // A collapsed run is one row standing for many: a faint centred tick, not a block.
                using (context.PushOpacity(0.5))
                {
                    context.FillRectangle(brush, new Rect(width / 3, top, width / 3, markerHeight));
                }
            }
            else
            {
                context.FillRectangle(brush, new Rect(0, top, width, Math.Max(markerHeight, (i - start) * rowHeight)));
            }
        }

        if (ViewportSize is > 0 and < 1)
        {
            var box = new Rect(0.5, (ViewportStart * height) + 0.5, Math.Max(0, width - 1), Math.Max(4, (ViewportSize * height) - 1));
            using (context.PushOpacity(0.6))
            {
                context.DrawRectangle(null, new Pen(Find("TextPrimaryBrush", Brushes.White), 1), box);
            }
        }

        IBrush? BrushFor(UnimusDiffLineViewModel line) =>
            line.IsRemoved ? removed : line.IsAdded ? added : line.IsHidden ? hidden : null;
    }

    private IBrush Find(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            e.Pointer.Capture(this);
            Navigate(e.GetPosition(this).Y);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Navigate(e.GetPosition(this).Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Navigate(double y)
    {
        if (Bounds.Height > 0)
        {
            NavigateRequested?.Invoke(this, Math.Clamp(y / Bounds.Height, 0, 1));
        }
    }
}
