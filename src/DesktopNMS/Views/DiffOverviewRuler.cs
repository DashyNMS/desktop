using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// A thin strip beside the Unimus tab's diff viewer (issue #115 follow-up)
/// showing where every change sits in the whole diff - red for removed
/// lines, green for added - plus a box for the part currently scrolled into
/// view, like an editor's scrollbar change markers. Click or drag on it to
/// jump there. Drawn directly in OnRender rather than as an ItemsControl of
/// rectangles: a config diff can be thousands of rows long.
/// </summary>
public sealed class DiffOverviewRuler : FrameworkElement
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(IReadOnlyList<UnimusDiffLineViewModel>),
        typeof(DiffOverviewRuler),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewportStartProperty = DependencyProperty.Register(
        nameof(ViewportStart),
        typeof(double),
        typeof(DiffOverviewRuler),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewportSizeProperty = DependencyProperty.Register(
        nameof(ViewportSize),
        typeof(double),
        typeof(DiffOverviewRuler),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<UnimusDiffLineViewModel>? Lines
    {
        get => (IReadOnlyList<UnimusDiffLineViewModel>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>Top of the visible part of the diff, as a fraction (0-1) of the whole.</summary>
    public double ViewportStart
    {
        get => (double)GetValue(ViewportStartProperty);
        set => SetValue(ViewportStartProperty, value);
    }

    /// <summary>Height of the visible part of the diff, as a fraction (0-1) of the whole.</summary>
    public double ViewportSize
    {
        get => (double)GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    /// <summary>Raised with the clicked position as a fraction (0-1) of the whole diff.</summary>
    public event EventHandler<double>? NavigateRequested;

    public DiffOverviewRuler()
    {
        Cursor = Cursors.Hand;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var height = ActualHeight;
        var width = ActualWidth;

        // A transparent fill so the whole strip is hit-testable, not just the markers.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (Lines is not { Count: > 0 } lines || height <= 0)
        {
            return;
        }

        var removed = TryFindResource("CriticalBrush") as Brush ?? Brushes.IndianRed;
        var added = TryFindResource("OkBrush") as Brush ?? Brushes.SeaGreen;
        var hidden = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var rowHeight = height / lines.Count;
        var markerHeight = Math.Max(2, rowHeight);

        // Consecutive rows of the same kind are merged into one rectangle -
        // far fewer draw calls on a long change block.
        var i = 0;
        while (i < lines.Count)
        {
            var brush = BrushFor(lines[i], removed, added, hidden);
            var start = i;
            while (i < lines.Count && BrushFor(lines[i], removed, added, hidden) == brush)
            {
                i++;
            }

            if (brush is not null)
            {
                var top = start * rowHeight;
                var rect = new Rect(0, top, width, Math.Max(markerHeight, (i - start) * rowHeight));
                if (ReferenceEquals(brush, hidden))
                {
                    // A collapsed run is a single row but stands for many -
                    // a faint centred tick rather than a full-width block.
                    rect = new Rect(width / 3, top, width / 3, markerHeight);
                    dc.PushOpacity(0.5);
                    dc.DrawRectangle(brush, null, rect);
                    dc.Pop();
                }
                else
                {
                    dc.DrawRectangle(brush, null, rect);
                }
            }
        }

        if (ViewportSize is > 0 and < 1)
        {
            var border = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
            var pen = new Pen(border, 1);
            var box = new Rect(0.5, ViewportStart * height + 0.5, Math.Max(0, width - 1), Math.Max(4, ViewportSize * height - 1));
            dc.PushOpacity(0.6);
            dc.DrawRectangle(null, pen, box);
            dc.Pop();
        }
    }

    private static Brush? BrushFor(UnimusDiffLineViewModel line, Brush removed, Brush added, Brush hidden) =>
        line.IsRemoved ? removed : line.IsAdded ? added : line.IsHidden ? hidden : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        Navigate(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
        {
            Navigate(e.GetPosition(this).Y);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void Navigate(double y)
    {
        if (ActualHeight > 0)
        {
            NavigateRequested?.Invoke(this, Math.Clamp(y / ActualHeight, 0, 1));
        }
    }
}
