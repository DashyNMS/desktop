using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

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
        if (DataContext is not DeviceListViewModel vm || string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            return;
        }

        e.Handled = true;

        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        vm.DevicesView.SortDescriptions.Clear();
        vm.DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceItemViewModel.IsPinned), ListSortDirection.Descending));
        vm.DevicesView.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, direction));

        foreach (var column in DeviceGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction;
    }
}
