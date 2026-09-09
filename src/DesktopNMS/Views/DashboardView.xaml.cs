using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopNMS.Core.Configuration;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Dashboard tab: at-a-glance widgets, starting with pinned sensors.</summary>
public partial class DashboardView : UserControl
{
    /// <summary>Widgets snap to this many pixels once a drag/resize ends, so edges line up.</summary>
    private const double GridSize = 20;

    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Dragging a widget's header moves it freely, clamped to the canvas edges
    /// and stopped by neighbouring widgets (with <see cref="DashboardWidget.Spacing"/>
    /// to spare) rather than allowed to overlap them; it snaps to the grid
    /// once released. X and Y are clamped independently so the widget can
    /// still slide along whichever axis is not blocked.
    /// </summary>
    private void OnHeaderDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DashboardWidgetViewModel widget)
        {
            return;
        }

        var bounds = FindAncestorCanvas((DependencyObject)sender);
        var maxX = bounds is null ? double.MaxValue : Math.Max(0, bounds.ActualWidth - widget.Width);
        var maxY = bounds is null ? double.MaxValue : Math.Max(0, bounds.ActualHeight - widget.Height);

        var candidateX = Math.Clamp(widget.X + e.HorizontalChange, 0, maxX);
        var candidateY = Math.Clamp(widget.Y + e.VerticalChange, 0, maxY);

        var others = GetOtherWidgetRects(widget).ToList();

        if (!OverlapsAny(new Rect(candidateX, widget.Y, widget.Width, widget.Height), others))
        {
            widget.X = candidateX;
        }

        if (!OverlapsAny(new Rect(widget.X, candidateY, widget.Width, widget.Height), others))
        {
            widget.Y = candidateY;
        }
    }

    private void OnHeaderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DashboardWidgetViewModel widget)
        {
            return;
        }

        widget.X = SnapToGrid(widget.X);
        widget.Y = SnapToGrid(widget.Y);
        widget.CommitPosition();
    }

    /// <summary>
    /// Dragging the corner grip resizes freely, clamped so the widget can
    /// never grow past the canvas's right/bottom edge or into a neighbour
    /// (with spacing to spare); it snaps to the grid once released. Width and
    /// height are clamped independently so it can still grow in whichever
    /// direction is not blocked.
    /// </summary>
    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DashboardWidgetViewModel widget)
        {
            return;
        }

        var bounds = FindAncestorCanvas((DependencyObject)sender);
        var maxWidth = bounds is null ? double.MaxValue : Math.Max(DashboardWidget.MinWidth, bounds.ActualWidth - widget.X);
        var maxHeight = bounds is null ? double.MaxValue : Math.Max(DashboardWidget.MinHeight, bounds.ActualHeight - widget.Y);

        var candidateWidth = Math.Clamp(widget.Width + e.HorizontalChange, DashboardWidget.MinWidth, maxWidth);
        var candidateHeight = Math.Clamp(widget.Height + e.VerticalChange, DashboardWidget.MinHeight, maxHeight);

        var others = GetOtherWidgetRects(widget).ToList();

        if (!OverlapsAny(new Rect(widget.X, widget.Y, candidateWidth, widget.Height), others))
        {
            widget.Width = candidateWidth;
        }

        if (!OverlapsAny(new Rect(widget.X, widget.Y, widget.Width, candidateHeight), others))
        {
            widget.Height = candidateHeight;
        }
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DashboardWidgetViewModel widget)
        {
            return;
        }

        widget.Width = Math.Max(DashboardWidget.MinWidth, SnapToGrid(widget.Width));
        widget.Height = Math.Max(DashboardWidget.MinHeight, SnapToGrid(widget.Height));
        widget.CommitSize();
    }

    private static double SnapToGrid(double value) => Math.Round(value / GridSize) * GridSize;

    /// <summary>Every other widget's current bounds, for collision checks while dragging/resizing one of them.</summary>
    private IEnumerable<Rect> GetOtherWidgetRects(DashboardWidgetViewModel moving)
    {
        if (DataContext is not DashboardViewModel viewModel)
        {
            yield break;
        }

        foreach (var other in viewModel.Widgets)
        {
            if (!ReferenceEquals(other, moving))
            {
                yield return new Rect(other.X, other.Y, other.Width, other.Height);
            }
        }
    }

    private static bool OverlapsAny(Rect candidate, IEnumerable<Rect> others)
    {
        candidate.Inflate(DashboardWidget.Spacing, DashboardWidget.Spacing);
        return others.Any(candidate.IntersectsWith);
    }

    /// <summary>Walks up the visual tree to find the Canvas the widgets are laid out on.</summary>
    private static Canvas? FindAncestorCanvas(DependencyObject start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Canvas canvas)
            {
                return canvas;
            }
        }

        return null;
    }
}
