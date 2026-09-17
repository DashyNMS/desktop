using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

    /// <summary>Restores a saved column layout - sort goes through <see cref="ApplyPinnedFirstSort"/> instead of <see cref="DataGridLayoutHelper"/>'s own generic sort restore, so pinned devices stay on top of whatever sort is restored, same as a live column click.</summary>
    public void ApplyGridLayout(GridLayout? layout)
    {
        DataGridLayoutHelper.Apply(DeviceGrid, layout, restoreSort: false);

        if (layout?.SortColumnIndex is { } index && index >= 0 && index < DeviceGrid.Columns.Count)
        {
            ApplyPinnedFirstSort(DeviceGrid.Columns[index], layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        }
    }

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(DeviceGrid);

    /// <summary>Puts the caret in the search box. Called by the shell window on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeviceListViewModel viewModel && viewModel.ShowDeviceDetailCommand.CanExecute(null))
        {
            viewModel.ShowDeviceDetailCommand.Execute(null);
        }
    }

    /// <summary>
    /// Shift-clicking a status badge isolates that status instead of toggling
    /// it normally - handled here (rather than a Command) so the plain click
    /// still flips <c>IsChecked</c> via its binding unchanged.
    /// </summary>
    private void StatusBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        if (sender is not ToggleButton { Tag: DeviceState state } || DataContext is not DeviceListViewModel vm)
        {
            return;
        }

        vm.IsolateState(state);
        e.Handled = true;
    }

    /// <summary>
    /// Overrides the DataGrid's default column-click sorting so pinned
    /// devices stay on top no matter which column is sorted: the default
    /// behaviour would otherwise replace DeviceListViewModel's IsPinned
    /// SortDescription with just the clicked column's, since a DataGrid
    /// bound to an ICollectionView normally manages SortDescriptions itself.
    /// </summary>
    private void DeviceGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not DeviceListViewModel || string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            return;
        }

        e.Handled = true;

        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        ApplyPinnedFirstSort(e.Column, direction);
    }

    /// <summary>
    /// Sorts by pinned-first, then the given column - shared by a live
    /// column click (<see cref="DeviceGrid_Sorting"/>) and restoring a saved
    /// sort column (<see cref="ApplyGridLayout"/>), so pinned devices stay on
    /// top no matter which triggered it. The default DataGrid-bound-to-an-
    /// ICollectionView behaviour would otherwise replace
    /// DeviceListViewModel's IsPinned SortDescription with just the clicked
    /// column's.
    /// </summary>
    private void ApplyPinnedFirstSort(DataGridColumn column, ListSortDirection direction)
    {
        if (DataContext is not DeviceListViewModel vm || string.IsNullOrEmpty(column.SortMemberPath))
        {
            return;
        }

        vm.DevicesView.SortDescriptions.Clear();
        vm.DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceItemViewModel.IsPinned), ListSortDirection.Descending));
        vm.DevicesView.SortDescriptions.Add(new SortDescription(column.SortMemberPath, direction));

        foreach (var gridColumn in DeviceGrid.Columns)
        {
            gridColumn.SortDirection = null;
        }

        column.SortDirection = direction;
    }
}
