using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Applies/captures a DataGrid's column widths, visibility, order and sort
/// against a persisted <see cref="GridLayout"/> - the shared plumbing
/// behind every grid's "remember how I left it" behaviour (issue #15).
/// Widths/visibility are matched by position, not a separately invented id
/// - see <see cref="GridLayout"/>'s own remarks. Order is handled
/// separately via <see cref="GridLayout.ColumnOrder"/> - see the same
/// remarks for why reassigning DisplayIndex from code needs care.
/// </summary>
public static class DataGridLayoutHelper
{
    /// <summary>
    /// Restores column widths/visibility and order from a saved layout,
    /// and - unless the caller has its own sort rule to apply instead (see
    /// DevicesView's pinned-row override) - the saved sort column/direction
    /// too.
    /// </summary>
    public static void Apply(DataGrid grid, GridLayout? layout, bool restoreSort = true)
    {
        if (layout is null)
        {
            return;
        }

        for (var i = 0; i < layout.Columns.Count && i < grid.Columns.Count; i++)
        {
            var saved = layout.Columns[i];
            var column = grid.Columns[i];

            // A star-sized column's saved Width is the star VALUE, not a
            // pixel count - reapplying it as a fixed pixel width would
            // permanently turn a "fill remaining space" column into a fixed
            // one the first time its layout was ever saved.
            column.Width = saved.IsStarWidth
                ? new DataGridLength(saved.Width, DataGridLengthUnitType.Star)
                : new DataGridLength(saved.Width, DataGridLengthUnitType.Pixel);

            column.Visibility = saved.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        ApplyColumnOrder(grid, layout.ColumnOrder);

        if (restoreSort && layout.SortColumnIndex is { } index && index >= 0 && index < grid.Columns.Count)
        {
            ApplySort(grid, grid.Columns[index], layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        }
    }

    /// <summary>
    /// Reassigns DisplayIndex from a saved order (issue #40) - each entry
    /// is a declaration-index, listed in ascending target-DisplayIndex
    /// order. Assigning 0, 1, 2, ... in that exact sequence is always safe:
    /// WPF shifts every other column's DisplayIndex to keep the set a valid
    /// permutation on each individual assignment, so by construction every
    /// assignment here is either already correct or a single valid shift -
    /// never the invalid in-between state that made a naive (declaration-
    /// order) restore loop crash live before. <paramref name="order"/> is
    /// validated as a genuine permutation before anything is touched, so a
    /// hand-edited or stale settings.json (wrong length, duplicate or
    /// out-of-range entries) is simply ignored rather than risking a throw.
    /// </summary>
    private static void ApplyColumnOrder(DataGrid grid, List<int>? order)
    {
        if (order is null || order.Count != grid.Columns.Count)
        {
            return;
        }

        var seen = new HashSet<int>();
        foreach (var index in order)
        {
            if (index < 0 || index >= grid.Columns.Count || !seen.Add(index))
            {
                return;
            }
        }

        for (var displayIndex = 0; displayIndex < order.Count; displayIndex++)
        {
            grid.Columns[order[displayIndex]].DisplayIndex = displayIndex;
        }
    }

    /// <summary>
    /// Applies a single column's sort to both the bound view and the header
    /// arrow - the generic path used by every grid except Devices, which
    /// layers its own pinned-first rule on top (see DevicesView.xaml.cs).
    /// </summary>
    public static void ApplySort(DataGrid grid, DataGridColumn column, ListSortDirection direction)
    {
        if (string.IsNullOrEmpty(column.SortMemberPath) || grid.ItemsSource is null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, direction));

        foreach (var c in grid.Columns)
        {
            c.SortDirection = null;
        }

        column.SortDirection = direction;
    }

    /// <summary>
    /// Reads back a grid's current column widths and whichever column is
    /// currently sorted (already tracked today via each column's own
    /// SortDirection, maintained by whichever sort path - generic or a
    /// special case like Devices' - is in play), or null when the grid is
    /// not currently visible/rendered - e.g. a different MainWindow tab or
    /// Device Details section is showing. A collapsed DataGrid is never
    /// measured, so its columns' ActualWidth reads back as whatever
    /// leftover/default value WPF happens to hold (confirmed live: several
    /// fixed columns read back as exactly DataGridColumn's own default
    /// MinWidth of 20) - capturing that and overwriting a perfectly good
    /// previous layout with it, every time the window closes while looking
    /// at something else, is what actually broke column sizing.
    /// </summary>
    public static GridLayout? Capture(DataGrid grid)
    {
        if (!grid.IsVisible || grid.ActualWidth <= 0)
        {
            return null;
        }

        var layout = new GridLayout();

        foreach (var column in grid.Columns)
        {
            layout.Columns.Add(new GridColumnLayout
            {
                Width = column.Width.IsStar ? column.Width.Value : column.ActualWidth,
                IsStarWidth = column.Width.IsStar,
                IsVisible = column.Visibility == Visibility.Visible,
            });
        }

        layout.ColumnOrder = grid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => grid.Columns.IndexOf(c))
            .ToList();

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            if (grid.Columns[i].SortDirection is { } direction)
            {
                layout.SortColumnIndex = i;
                layout.SortDescending = direction == ListSortDirection.Descending;
                break;
            }
        }

        return layout;
    }
}
