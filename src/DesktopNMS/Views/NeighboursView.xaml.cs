using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Neighbours tab - see <see cref="NeighboursViewModel"/>.</summary>
public partial class NeighboursView : UserControl
{
    public NeighboursView()
    {
        InitializeComponent();

        // The time range picker only matters while the graphs are showing.
        DataContextChanged += (_, _) => UpdateTimeRangeVisibility();
    }

    /// <summary>Focuses whichever search box is showing - the views table's, or the open view's.</summary>
    public void FocusSearch()
    {
        var box = DataContext is NeighboursViewModel { IsViewListMode: true } ? ViewSearchBox : SearchBox;
        box.Focus();
        box.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(NeighboursGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(NeighboursGrid);

    private void UpdateTimeRangeVisibility()
    {
        if (DataContext is NeighboursViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            GraphTimeRange.Visibility = vm.IsGraphsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NeighboursViewModel.IsGraphsCollapsed))
        {
            UpdateTimeRangeVisibility();
        }
    }

    private void StateBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift
            || sender is not ToggleButton { Tag: NeighbourState state }
            || DataContext is not NeighboursViewModel vm)
        {
            return;
        }

        vm.IsolateState(state);
        e.Handled = true;
    }

    /// <summary>Right-clicking a column header offers every column to show or hide, as on the Devices tab.</summary>
    private void NeighboursGrid_HeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header)
        {
            return;
        }

        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = header };
        foreach (var column in NeighboursGrid.Columns)
        {
            if (column.Header is not string name || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible };
            item.Click += (_, _) =>
            {
                // Never down to no columns at all.
                if (!item.IsChecked && NeighboursGrid.Columns.Count(c => c.Visibility == Visibility.Visible) <= 1)
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
}
