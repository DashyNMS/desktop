using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Applies/captures a DataGrid's column widths and sort against a persisted
/// <see cref="GridLayout"/> - the shared plumbing behind every grid's
/// "remember how I left it" behaviour (issue #15). Columns are matched by
/// position, not a separately invented id - see <see cref="GridLayout"/>'s
/// own remarks. Column order is deliberately not part of this - see the
/// same remarks for why reassigning DisplayIndex from code is unsafe.
/// </summary>
public static class DataGridLayoutHelper
{
    /// <summary>
    /// Restores column widths from a saved layout, and - unless the caller
    /// has its own sort rule to apply instead (see DevicesView's pinned-row
    /// override) - the saved sort column/direction too.
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
        }

        if (restoreSort && layout.SortColumnIndex is { } index && index >= 0 && index < grid.Columns.Count)
        {
            ApplySort(grid, grid.Columns[index], layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending);
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
            });
        }

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
