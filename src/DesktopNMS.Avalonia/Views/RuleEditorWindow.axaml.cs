using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class RuleEditorWindow : Window
{
    /// <summary>Height of a group's header strip: a drop on it means "into this group, at the top", not "beside it".</summary>
    private const double GroupHeaderHeight = 36;

    /// <summary>How far the pointer moves before a press on a grip becomes a drag, so a plain click does nothing.</summary>
    private const double DragThreshold = 4;

    private readonly RuleEditorViewModel? _viewModel;

    private object? _dragged;
    private Point _dragOrigin;
    private bool _dragging;
    private Control? _hovered;

    /// <summary>For the designer.</summary>
    public RuleEditorWindow()
    {
        InitializeComponent();
    }

    public RuleEditorWindow(RuleEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => NameBox.Focus();

        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
    }

    /// <summary>True once saved; closing any other way changes nothing.</summary>
    public bool Saved { get; private set; }

    // ------------------------------------------------------------------
    // Drag to reorder (#22): a condition's or group's grip picks it up, and
    // it drops onto whichever condition or group the pointer is released over -
    // in the app, not through the OS's drag and drop.

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: { } item } grip && e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            _dragged = item;
            _dragOrigin = e.GetPosition(this);
            _dragging = false;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_dragging)
        {
            var moved = position - _dragOrigin;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        var target = DropTargetAt(position);
        if (!ReferenceEquals(target, _hovered))
        {
            _hovered?.Classes.Remove("dropHover");
            _hovered = target;
            _hovered?.Classes.Add("dropHover");
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is { } dragged && _dragging && DropTargetAt(e.GetPosition(this)) is { DataContext: { } target } element
            && !ReferenceEquals(target, dragged))
        {
            var position = e.GetPosition(element);
            var intoGroup = target is RuleConditionGroupViewModel && position.Y < GroupHeaderHeight;
            var placeAfter = position.Y > element.Bounds.Height / 2;
            _viewModel?.MoveCondition(dragged, target, placeAfter, intoGroup);
        }

        e.Pointer.Capture(null);
        EndDrag();
    }

    /// <summary>The innermost condition or group under the pointer.</summary>
    private Control? DropTargetAt(Point position)
        => this.GetVisualsAt(position)
            .SelectMany(v => v.GetSelfAndVisualAncestors())
            .OfType<Control>()
            .FirstOrDefault(c => c.Classes.Contains("dropTarget"));

    private void EndDrag()
    {
        _dragged = null;
        _dragging = false;
        _hovered?.Classes.Remove("dropHover");
        _hovered = null;
        Cursor = null;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Saved = saved;
        Close();
    }
}
