using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Lets every table sort by any column that names what to sort by (#250).
/// The DataGrid decides a column's sortability up front from the declared row
/// type of its list, and the view models' FilteredView is a list of plain
/// <see cref="object"/> - so it finds no "Name" or "State" there and turns
/// sorting off everywhere, although the sort itself reads each row's own value
/// and works. A column with a SortMemberPath is sortable; one without (a pin,
/// the edit buttons) isn't.
/// </summary>
public static class DataGridSorting
{
    /// <summary>Once, at start-up.</summary>
    public static void Register() =>
        Control.LoadedEvent.AddClassHandler<DataGrid>((grid, _) => Enable(grid), RoutingStrategies.Direct);

    private static void Enable(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            if (!string.IsNullOrEmpty(column.SortMemberPath))
            {
                column.CanUserSort = true;
            }
        }
    }
}
