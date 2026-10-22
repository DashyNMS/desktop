using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DevicesView : UserControl
{
    private DeviceListViewModel? _viewModel;
    private DataGridColumn? _sortColumn;
    private ListSortDirection _sortDirection;
    private DataGridColumnHeader? _rightClickedHeader;

    public DevicesView()
    {
        InitializeComponent();

        DeviceGrid.SelectionChanged += (_, _) =>
            _viewModel?.UpdateSelectedDevices(DeviceGrid.SelectedItems.OfType<DeviceItemViewModel>());
        DeviceGrid.DoubleTapped += OnGridDoubleTapped;
        DeviceGrid.Sorting += OnGridSorting;

        // The rows' menu belongs to the whole grid and takes every right-click,
        // so a right-press on a header is noted here, and its opening is turned
        // into the column picker instead.
        DeviceGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        if (DeviceGrid.ContextMenu is { } rowMenu)
        {
            rowMenu.Opening += OnRowMenuOpening;
        }

        // Tunnelling, so a shift-click on a state count is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DeviceListViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyPinColumnVisibility();
    }

    /// <summary>The pin column follows Settings' pinned devices option (#98); a column can't be bound, so it's set here.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceListViewModel.PinningEnabled))
        {
            ApplyPinColumnVisibility();
        }
    }

    private void ApplyPinColumnVisibility() => DeviceGrid.Columns[0].IsVisible = _viewModel?.PinningEnabled != false;

    /// <summary>A double-click on a row opens the device.</summary>
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null
            && _viewModel?.ShowDeviceDetailCommand.CanExecute(null) == true)
        {
            _viewModel.ShowDeviceDetailCommand.Execute(null);
        }
    }

    /// <summary>
    /// Sorting by a column keeps pinned devices on top: the grid's own sort
    /// would replace the list's pinned-first order with just the column's.
    /// A first click sorts ascending, the next descending, and so on.
    /// </summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Column.SortMemberPath) || DeviceGrid.CollectionView is not DataGridCollectionView view)
        {
            return;
        }

        e.Handled = true;
        _sortDirection = ReferenceEquals(_sortColumn, e.Column) && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortColumn = e.Column;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            // Comparers, not paths: see DataGridSorting for why a path sort reads nothing here.
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new PropertyComparer(nameof(DeviceItemViewModel.IsPinned)), ListSortDirection.Descending));
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new PropertyComparer(e.Column.SortMemberPath), _sortDirection));
        }
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _rightClickedHeader = e.GetCurrentPoint(DeviceGrid).Properties.IsRightButtonPressed
            ? (e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true)
            : null;
    }

    /// <summary>
    /// Right-clicking a column header picks the columns shown (#40); anywhere
    /// else in the grid gets the rows' own menu as usual.
    /// </summary>
    private void OnRowMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_rightClickedHeader is not { } header)
        {
            return;
        }

        _rightClickedHeader = null;
        e.Cancel = true;
        ShowColumnsMenu(header);
    }

    private void ShowColumnsMenu(Control header)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();

        // Built fresh each time from the grid's own columns; the pin column has no name to show.
        foreach (var column in DeviceGrid.Columns.Where(c => c.Header is string { Length: > 0 }))
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = (string)column.Header! },
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
            };

            item.Click += (_, _) =>
            {
                // Never hide the last column showing.
                if (column.IsVisible && DeviceGrid.Columns.Count(c => c.IsVisible && c.Header is string { Length: > 0 }) <= 1)
                {
                    item.IsChecked = true;
                    return;
                }

                column.IsVisible = !column.IsVisible;
                item.IsChecked = column.IsVisible;
            };

            items.Add(item);
        }

        menu.ItemsSource = items;
        menu.Open(header);
    }

    /// <summary>Shift-clicking a state count shows only that state instead of toggling it.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && _viewModel is not null
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: DeviceState state } badge
            && badge.Classes.Contains("state"))
        {
            _viewModel.IsolateState(state);
            e.Handled = true;
        }
    }
}
