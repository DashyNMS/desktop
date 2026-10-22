using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Lets every table sort by any column that names what to sort by (#250).
/// The DataGrid learns a column's property type from the declared row type of
/// its list, and the view models' FilteredView is a list of plain
/// <see cref="object"/>. So it finds no "Name" or "State" there: it turns
/// sorting off, and even when it's turned on its own path sort reads every
/// value as null. Each column with a SortMemberPath is made sortable with a
/// <see cref="PropertyComparer"/> that reads the value from the row itself;
/// one without (a pin, the edit buttons) isn't.
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
                column.CustomSortComparer ??= new PropertyComparer(column.SortMemberPath);
            }
        }
    }
}

/// <summary>
/// Orders rows by a property (or dotted path) read from each row's own type:
/// text in the current culture, ignoring case; anything else comparable by its
/// own order; nothing first.
/// </summary>
public sealed class PropertyComparer(string propertyPath) : IComparer
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?[]> Paths = new();

    public int Compare(object? x, object? y)
    {
        var a = ValueOf(x);
        var b = ValueOf(y);

        return (a, b) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (string s, string t) => string.Compare(s, t, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase),
            _ => Comparer.Default.Compare(a, b),
        };
    }

    private object? ValueOf(object? item)
    {
        if (item is null)
        {
            return null;
        }

        var chain = Paths.GetOrAdd((item.GetType(), propertyPath), static key =>
        {
            var properties = new List<PropertyInfo?>();
            var type = key.Item1;
            foreach (var name in key.Item2.Split('.'))
            {
                var property = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                properties.Add(property);
                type = property?.PropertyType;
            }

            return properties.ToArray();
        });

        object? value = item;
        foreach (var property in chain)
        {
            if (value is null || property is null)
            {
                return null;
            }

            value = property.GetValue(value);
        }

        return value;
    }
}
