using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// "Remember how I left it" for a table (#15, #250, from the WPF app's
/// DataGridLayoutHelper): its column widths, which are shown, their order and
/// the column it's sorted by, saved under a name when its window closes and
/// put back when it next appears:
/// <code>&lt;DataGrid infra:GridLayoutMemory.Key="Devices" /&gt;</code>
/// The names are the WPF app's, so a layout carries across. Columns are
/// matched by position, so a saved layout with a different number of columns
/// (a table that's gained or lost one) is ignored rather than misapplied.
/// </summary>
public static class GridLayoutMemory
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, string?>("Key", typeof(GridLayoutMemory));

    /// <summary>
    /// Leave a grid's saved sort alone - for a table with its own sort rule
    /// on top (Devices keeps pinned devices first), which restores it itself.
    /// </summary>
    public static readonly AttachedProperty<bool> SkipSortProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("SkipSort", typeof(GridLayoutMemory));

    /// <summary>The column last sorted by the person, and which way - Avalonia's columns don't say.</summary>
    private static readonly AttachedProperty<(int Column, bool Descending)?> SortedProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, (int Column, bool Descending)?>("Sorted", typeof(GridLayoutMemory));

    /// <summary>Set once at start-up.</summary>
    public static ISettingsStore? Settings { get; set; }

    static GridLayoutMemory()
    {
        KeyProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.OldValue is null && e.NewValue is string)
            {
                grid.Loaded += OnLoaded;
                grid.Sorting += OnSorting;
            }
        });
    }

    public static string? GetKey(DataGrid grid) => grid.GetValue(KeyProperty);

    public static void SetKey(DataGrid grid, string? value) => grid.SetValue(KeyProperty, value);

    public static bool GetSkipSort(DataGrid grid) => grid.GetValue(SkipSortProperty);

    public static void SetSkipSort(DataGrid grid, bool value) => grid.SetValue(SkipSortProperty, value);

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || GetKey(grid) is not { } key)
        {
            return;
        }

        // Once per grid: Loaded comes again each time a tab is shown.
        grid.Loaded -= OnLoaded;

        if (TopLevel.GetTopLevel(grid) is Window window)
        {
            window.Closing += (_, _) => Save(grid, key);
        }

        if (Settings?.Current.GridLayouts.GetValueOrDefault(key) is { } layout)
        {
            // After the grid's own Loaded work (the sorting set-up), so a saved sort finds its comparer.
            Dispatcher.UIThread.Post(() => Apply(grid, layout), DispatcherPriority.Loaded);
        }
    }

    private static void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        // Read back once the grid (or the table's own handler) has sorted.
        var column = grid.Columns.IndexOf(e.Column);
        Dispatcher.UIThread.Post(() =>
        {
            if (grid.CollectionView?.SortDescriptions is { Count: > 0 } sorts)
            {
                grid.SetValue(SortedProperty, (column, sorts[^1].Direction == ListSortDirection.Descending));
            }
        });
    }

    private static void Apply(DataGrid grid, GridLayout layout)
    {
        try
        {
            if (layout.Columns.Count != grid.Columns.Count)
            {
                return;
            }

            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var saved = layout.Columns[i];
                var column = grid.Columns[i];

                // A star column's saved width is its share, not pixels - kept as a
                // share, so it still fills the space left.
                column.Width = saved.IsStarWidth
                    ? new DataGridLength(saved.Width, DataGridLengthUnitType.Star)
                    : new DataGridLength(Math.Max(saved.Width, column.MinWidth), DataGridLengthUnitType.Pixel);
                column.IsVisible = saved.IsVisible;
            }

            // A genuine reordering of every column, or nothing - a hand-edited or
            // stale entry is ignored rather than risking a broken order.
            if (layout.ColumnOrder is { } order && order.Count == grid.Columns.Count && order.Distinct().Count() == order.Count
                && order.All(i => i >= 0 && i < grid.Columns.Count))
            {
                for (var displayIndex = 0; displayIndex < order.Count; displayIndex++)
                {
                    grid.Columns[order[displayIndex]].DisplayIndex = displayIndex;
                }
            }

            if (!GetSkipSort(grid) && layout.SortColumnIndex is { } index && index >= 0 && index < grid.Columns.Count
                && grid.Columns[index] is { CanUserSort: true, CustomSortComparer: { } comparer }
                && grid.CollectionView is DataGridCollectionView view)
            {
                var direction = layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(DataGridSortDescription.FromComparer(comparer, direction));
                }

                grid.SetValue(SortedProperty, (index, layout.SortDescending));
            }
        }
        catch (Exception)
        {
            // A layout problem never stops the table showing.
        }
    }

    /// <summary>
    /// Only a table that's showing has real widths to save: one on a hidden tab
    /// or section was never laid out, so it's skipped and its last saved layout kept.
    /// </summary>
    private static void Save(DataGrid grid, string key)
    {
        try
        {
            if (Settings is not { } settings || !grid.IsEffectivelyVisible || grid.Bounds.Width <= 0)
            {
                return;
            }

            var layout = new GridLayout();
            foreach (var column in grid.Columns)
            {
                layout.Columns.Add(new GridColumnLayout
                {
                    Width = column.Width.IsStar ? column.Width.Value : column.ActualWidth,
                    IsStarWidth = column.Width.IsStar,
                    IsVisible = column.IsVisible,
                });
            }

            layout.ColumnOrder = grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => grid.Columns.IndexOf(c)).ToList();

            if (grid.GetValue(SortedProperty) is { } sorted)
            {
                layout.SortColumnIndex = sorted.Column;
                layout.SortDescending = sorted.Descending;
            }

            settings.Current.GridLayouts[key] = layout;
            settings.Save();
        }
        catch (Exception)
        {
            // Never let a layout problem block closing the window.
        }
    }
}
