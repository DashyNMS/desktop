using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Dashboard tab: widgets laid out on a snapped grid of whole cells
/// (<see cref="DashboardWidget.CellSize"/> pixels each). Dragging or resizing
/// one over another pushes that one down, so rearranging reflows the layout
/// rather than hunting for a gap.
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>A generous ceiling on column, row and span, so a wild drag can't grow the canvas without bound.</summary>
    private const int MaxGridExtent = 500;

    private DashboardViewModel? _viewModel;

    private DashboardWidgetViewModel? _movingWidget;
    private DashboardWidgetViewModel? _resizingWidget;
    private Point _gestureStart;
    private int _originColumn;
    private int _originRow;
    private int _originColumnSpan;
    private int _originRowSpan;

    public DashboardView()
    {
        InitializeComponent();

        WidgetsHost.ContainerPrepared += OnContainerPrepared;
        CanvasScroll.SizeChanged += (_, _) => UpdateCanvasExtent();

        // Tunnelling, so they see a click or key before the controls under it do.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private DashboardViewModel? ViewModel => DataContext as DashboardViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.Widgets.CollectionChanged -= OnWidgetsChanged;
            _viewModel.WidgetAdded -= OnWidgetAdded;
        }

        _viewModel = ViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Widgets.CollectionChanged += OnWidgetsChanged;
            _viewModel.WidgetAdded += OnWidgetAdded;
        }

        UpdateCanvasExtent();
    }

    /// <summary>Places each widget on the canvas at its cell.</summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        e.Container.Bind(Canvas.LeftProperty, new Binding(nameof(DashboardWidgetViewModel.Column)) { Converter = Converters.GridUnitToPixels });
        e.Container.Bind(Canvas.TopProperty, new Binding(nameof(DashboardWidgetViewModel.Row)) { Converter = Converters.GridUnitToPixels });
    }

    private void OnWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCanvasExtent();

    /// <summary>
    /// Sizes the canvas to the bigger of the viewport and the furthest widget,
    /// so dragging past the view scrolls rather than clips, and it shrinks back
    /// once nothing needs the room.
    /// </summary>
    private void UpdateCanvasExtent()
    {
        if (ViewModel is not { } viewModel || WidgetsHost.ItemsPanelRoot is not Canvas canvas)
        {
            return;
        }

        double width = 0;
        double height = 0;
        foreach (var widget in viewModel.Widgets)
        {
            width = Math.Max(width, (widget.Column + widget.ColumnSpan) * DashboardWidget.CellSize);
            height = Math.Max(height, (widget.Row + widget.RowSpan) * DashboardWidget.CellSize);
        }

        canvas.Width = Math.Max(CanvasScroll.Viewport.Width, width);
        canvas.Height = Math.Max(CanvasScroll.Viewport.Height, height);
    }

    /// <summary>Scrolls a widget just added from the picker into sight, once it's laid out.</summary>
    private void OnWidgetAdded(object? sender, DashboardWidgetViewModel widget)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                UpdateCanvasExtent();
                WidgetsHost.ContainerFromItem(widget)?.BringIntoView();
            },
            DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------ move and resize

    // The target cell is worked out afresh from the pointer's position over
    // this view (which never moves) each time, not by adding up deltas: once
    // a whole-cell jump moves the widget out from under the pointer, deltas
    // measured against the widget feed back into more jumps and it shakes.

    private void OnMovePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DashboardWidgetViewModel widget } handle && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            _movingWidget = widget;
            _gestureStart = e.GetPosition(this);
            _originColumn = widget.Column;
            _originRow = widget.Row;
            e.Pointer.Capture(handle);
            e.Handled = true;
        }
    }

    private void OnMoveMoved(object? sender, PointerEventArgs e)
    {
        if (_movingWidget is not { } widget)
        {
            return;
        }

        var (columns, rows) = CellsMoved(e);
        var column = Math.Clamp(_originColumn + columns, 0, MaxGridExtent);
        var row = Math.Clamp(_originRow + rows, 0, MaxGridExtent);
        if (column == widget.Column && row == widget.Row)
        {
            return;
        }

        widget.Column = column;
        widget.Row = row;
        AfterGestureStep(widget);
    }

    private void OnMoveReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndGesture();
    }

    private void OnMoveCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGesture();

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DashboardWidgetViewModel widget } handle && e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            _resizingWidget = widget;
            _gestureStart = e.GetPosition(this);
            _originColumnSpan = widget.ColumnSpan;
            _originRowSpan = widget.RowSpan;
            e.Pointer.Capture(handle);
            e.Handled = true;
        }
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingWidget is not { } widget)
        {
            return;
        }

        var (columns, rows) = CellsMoved(e);
        var columnSpan = Math.Clamp(_originColumnSpan + columns, DashboardWidget.MinColumnSpan, MaxGridExtent);
        var rowSpan = Math.Clamp(_originRowSpan + rows, DashboardWidget.MinRowSpan, MaxGridExtent);
        if (columnSpan == widget.ColumnSpan && rowSpan == widget.RowSpan)
        {
            return;
        }

        widget.ColumnSpan = columnSpan;
        widget.RowSpan = rowSpan;
        AfterGestureStep(widget);
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndGesture();
    }

    private void OnResizeCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGesture();

    private (int Columns, int Rows) CellsMoved(PointerEventArgs e)
    {
        var current = e.GetPosition(this);
        return ((int)Math.Round((current.X - _gestureStart.X) / DashboardWidget.CellSize),
                (int)Math.Round((current.Y - _gestureStart.Y) / DashboardWidget.CellSize));
    }

    private void AfterGestureStep(DashboardWidgetViewModel widget)
    {
        ViewModel?.Reflow(widget);
        ShowGuides(widget);
        UpdateCanvasExtent();
    }

    private void EndGesture()
    {
        if (_movingWidget is null && _resizingWidget is null)
        {
            return;
        }

        _movingWidget = null;
        _resizingWidget = null;
        GuideLayer.Children.Clear();
        ViewModel?.CommitLayout();
    }

    /// <summary>
    /// Alignment guides (#278): dashed lines where the widget being moved or
    /// resized lines up with another's edges - see DashboardGrid.AlignedEdges.
    /// </summary>
    private void ShowGuides(DashboardWidgetViewModel moving)
    {
        GuideLayer.Children.Clear();
        if (ViewModel is not { } viewModel || WidgetsHost.ItemsPanelRoot is not Canvas canvas)
        {
            return;
        }

        static GridItem Item(DashboardWidgetViewModel w) => new(w.Id, w.Column, w.Row, w.ColumnSpan, w.RowSpan);
        var (columns, rows) = DashboardGrid.AlignedEdges(Item(moving), viewModel.Widgets.Select(Item));
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : Brushes.DodgerBlue;

        Line Guide(Point from, Point to) => new()
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
        };

        foreach (var column in columns)
        {
            var x = column * DashboardWidget.CellSize;
            GuideLayer.Children.Add(Guide(new Point(x, 0), new Point(x, canvas.Bounds.Height)));
        }

        foreach (var row in rows)
        {
            var y = row * DashboardWidget.CellSize;
            GuideLayer.Children.Add(Guide(new Point(0, y), new Point(canvas.Bounds.Width, y)));
        }
    }

    // ------------------------------------------------------------ clicks and keys

    /// <summary>
    /// Before the controls see a press: in edit mode, a click on a widget
    /// selects it, so the keys act on it (#278); and shift-clicking an Alerts
    /// widget's severity chip shows only that severity instead of toggling it.
    /// </summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift
            && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is { DataContext: AlertsWidgetHeader header } chip
            && chip.Classes.Contains("severity"))
        {
            header.Widget.IsolateSeverity(chip.Tag as string == "Critical" ? AlertSeverity.Critical : AlertSeverity.Warning);
            e.Handled = true;
            return;
        }

        if (ViewModel is { IsEditMode: true } viewModel
            && source.GetSelfAndVisualAncestors().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("widget")) is { DataContext: DashboardWidgetViewModel widget })
        {
            viewModel.SelectedWidget = widget;
            if (source is not TextBox)
            {
                Focus();
            }
        }
    }

    /// <summary>
    /// The keys in edit mode (#278): arrows move the selected widget a cell,
    /// Shift + arrows resize it, Del removes it, F2 renames it, Ctrl+D
    /// duplicates it, Ctrl+Z undoes and Esc lets go of it. Typing in a box is
    /// left alone.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { IsEditMode: true } viewModel || e.Source is TextBox)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Z)
        {
            viewModel.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (viewModel.SelectedWidget is not { } widget)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                viewModel.Nudge(-1, 0, shift);
                break;
            case Key.Right:
                viewModel.Nudge(1, 0, shift);
                break;
            case Key.Up:
                viewModel.Nudge(0, -1, shift);
                break;
            case Key.Down:
                viewModel.Nudge(0, 1, shift);
                break;
            case Key.Delete or Key.Back:
                widget.RemoveCommand.Execute(null);
                break;
            case Key.F2:
                widget.RenameCommand.Execute(null);
                break;
            case Key.D when ctrl:
                widget.DuplicateCommand.Execute(null);
                break;
            case Key.Escape:
                viewModel.SelectedWidget = null;
                break;
            default:
                return;
        }

        UpdateCanvasExtent();
        e.Handled = true;
    }

    // ------------------------------------------------------------ renaming

    private void OnRenameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    /// <summary>Enter keeps the new title, Esc the old one; either way renaming ends.</summary>
    private void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DashboardWidgetViewModel widget } box || e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        var binding = BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty);
        if (e.Key == Key.Enter)
        {
            binding?.UpdateSource();
        }
        else
        {
            binding?.UpdateTarget();
        }

        widget.IsRenaming = false;
        e.Handled = true;
    }

    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DashboardWidgetViewModel widget })
        {
            widget.IsRenaming = false;
        }
    }
}
