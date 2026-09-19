using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class RuleEditorWindow : Window
{
    /// <summary>Clipboard format name for an in-process drag of a condition row/group - never leaves this window.</summary>
    private const string ConditionDragFormat = "DashyNMS.RuleCondition";

    /// <summary>Height of a group's header strip; a drop within it means "into this group, at the top" rather than "beside this group".</summary>
    private const double GroupHeaderHeight = 36;

    private readonly RuleEditorViewModel _viewModel;

    private object? _pendingDrag;
    private Point _pendingDragOrigin;

    public RuleEditorWindow(RuleEditorViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;

        Loaded += (_, _) => NameBox.Focus();

        // A drag is armed on the grip's mouse-down but only starts once the
        // pointer has actually moved (the standard WPF drag threshold), so a
        // plain click on the grip does nothing.
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => _pendingDrag = null;
    }

    /// <summary>
    /// Drops the field picker's list open as soon as the user starts typing.
    /// StaysOpenOnEdit only keeps an already-open dropdown open, so without
    /// this the filtering happens invisibly behind a closed popup.
    /// </summary>
    private void OnFieldPickerPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is ComboBox picker)
        {
            picker.IsDropDownOpen = true;
        }
    }

    // ------------------------------------------------------------------
    // Drag-to-reorder (issue #22): the grip on each row / group header arms
    // a drag of that item's view model; any row or group is a drop target.

    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: { } item })
        {
            _pendingDrag = item;
            _pendingDragOrigin = e.GetPosition(this);
        }
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDrag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _pendingDragOrigin;
        if (System.Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && System.Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pendingDrag;
        _pendingDrag = null;

        DragDrop.DoDragDrop(this, new DataObject(ConditionDragFormat, item), DragDropEffects.Move);
    }

    private void OnConditionDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ConditionDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnConditionDrop(object sender, DragEventArgs e)
    {
        // Handled at the innermost target so a drop on a row inside a group
        // doesn't also fire on the group's own border.
        e.Handled = true;

        if (!e.Data.GetDataPresent(ConditionDragFormat) || sender is not FrameworkElement { DataContext: { } target } element)
        {
            return;
        }

        var dragged = e.Data.GetData(ConditionDragFormat);
        if (dragged is null)
        {
            return;
        }

        var position = e.GetPosition(element);
        var intoGroup = target is RuleConditionGroupViewModel && position.Y < GroupHeaderHeight;
        var placeAfter = position.Y > element.ActualHeight / 2;

        _viewModel.MoveCondition(dragged, target, placeAfter, intoGroup);
    }

    private void OnRequestClose(object? sender, bool saved)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = saved;
    }
}
