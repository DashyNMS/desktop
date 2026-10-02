using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DevicesView : UserControl
{
    private DeviceListViewModel? _viewModel;

    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// The pin column follows Settings' pinned devices option (#98). A
    /// DataGrid column isn't in the visual tree, so its Visibility can't be
    /// bound - it's set here instead, and re-applied after a saved layout is
    /// restored, since that sets every column's visibility too.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as DeviceListViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyPinColumnVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceListViewModel.PinningEnabled))
        {
            ApplyPinColumnVisibility();
        }
    }

    private void ApplyPinColumnVisibility() =>
        PinColumn.Visibility = _viewModel?.PinningEnabled == false ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Restores a saved column layout - sort goes through <see cref="ApplyPinnedFirstSort"/> instead of <see cref="DataGridLayoutHelper"/>'s own generic sort restore, so pinned devices stay on top of whatever sort is restored, same as a live column click.</summary>
    public void ApplyGridLayout(GridLayout? layout)
    {
        DataGridLayoutHelper.Apply(DeviceGrid, layout, restoreSort: false);
        ApplyPinColumnVisibility();

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
    /// Forwards the grid's multi-selection to the view model.
    /// DataGrid.SelectedItems is not a dependency property, so it cannot be
    /// bound directly - this is the standard way to bridge it into MVVM,
    /// same as AlertsView.xaml.cs's OnGridSelectionChanged.
    /// </summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DeviceListViewModel viewModel)
        {
            viewModel.UpdateSelectedDevices(DeviceGrid.SelectedItems.Cast<DeviceItemViewModel>());
        }
    }

    /// <summary>
    /// Right-clicking a column header opens the show/hide menu (issue #40) -
    /// the standard place for this in most grid-based apps, rather than a
    /// separate toolbar button. Right-clicking anywhere else in the grid
    /// (a row/cell) is left alone, so DataGrid.ContextMenu still shows there
    /// as normal.
    /// </summary>
    private void DeviceGrid_HeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header)
        {
            return;
        }

        e.Handled = true;
        ShowColumnsMenu(header);
    }

    /// <summary>
    /// Builds the column show/hide menu fresh on every open, straight from
    /// DeviceGrid's own live columns - a headerless column (just the pin
    /// toggle) is skipped, since there is nothing to label it with and no
    /// reason to hide it. Refuses to hide the last remaining visible column
    /// so the grid can never end up fully empty.
    /// </summary>
    private void ShowColumnsMenu(UIElement placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };

        foreach (var column in DeviceGrid.Columns)
        {
            if (column.Header is not string header || string.IsNullOrEmpty(header))
            {
                continue;
            }

            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
            };

            item.Click += (_, _) =>
            {
                if (!item.IsChecked && DeviceGrid.Columns.Count(c => c.Visibility == Visibility.Visible) <= 1)
                {
                    item.IsChecked = true;
                    return;
                }

                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as T;
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
