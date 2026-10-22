using System.Collections;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// Device Details' Ports: grouped by the chosen key (#272) - a view concern,
/// on the grid's own view of the ports, so search and column sorting still
/// work inside each group - and a port picked from elsewhere scrolled to.
/// </summary>
public partial class PortsSection : UserControl
{
    private DeviceDetailViewModel? _viewModel;
    private DataGridColumn? _sortColumn;
    private ListSortDirection _sortDirection;

    public PortsSection()
    {
        InitializeComponent();
        PortsGrid.Sorting += OnGridSorting;
        PortsGrid.Loaded += (_, _) => ApplyGrouping();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DeviceDetailViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _sortColumn = null;
        ApplyGrouping();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceDetailViewModel.PortGroupProperty))
        {
            ApplyGrouping();
        }
        else if (e.PropertyName == nameof(DeviceDetailViewModel.SelectedPort) && _viewModel?.SelectedPort is { } port)
        {
            PortsGrid.ScrollIntoView(port, null);
        }
    }

    /// <summary>Groups by the chosen key, the groups in their natural order - Up before Down, VLANs and speeds by number.</summary>
    private void ApplyGrouping()
    {
        if (_viewModel is null || PortsGrid.CollectionView is not DataGridCollectionView view)
        {
            return;
        }

        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (_viewModel.PortGroupProperty is { } property)
            {
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(property));
            }

            ApplySort(view);
        }
    }

    /// <summary>A column click sorts within the groups, which keep their own order.</summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Column.SortMemberPath) || PortsGrid.CollectionView is not DataGridCollectionView view)
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
            ApplySort(view);
        }
    }

    private void ApplySort(DataGridCollectionView view)
    {
        view.SortDescriptions.Clear();
        if (_viewModel?.PortGroupProperty is { } property)
        {
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new GroupOrder(_viewModel.PortGroupingValue, property)));
        }

        if (_sortColumn is { SortMemberPath: { Length: > 0 } path })
        {
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new PropertyComparer(path), _sortDirection));
        }
    }

    /// <summary>Orders ports by their group's place: Up before Down, VLANs and speeds by number, types by name.</summary>
    private sealed class GroupOrder(PortGrouping grouping, string property) : IComparer
    {
        private readonly PropertyComparer _byName = new(property);

        public int Compare(object? x, object? y)
        {
            if (x is not PortItemViewModel a || y is not PortItemViewModel b)
            {
                return 0;
            }

            var byKey = a.GroupSortKey(grouping).CompareTo(b.GroupSortKey(grouping));
            return byKey != 0 ? byKey : _byName.Compare(a, b);
        }
    }
}
