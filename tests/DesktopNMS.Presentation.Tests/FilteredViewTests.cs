using System.Collections.ObjectModel;
using System.Collections.Specialized;
using DesktopNMS.Infrastructure;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public class FilteredViewTests
{
    private sealed record Item(string Name, bool Pinned);

    private static ObservableCollection<Item> Fleet() => new()
    {
        new("core-sw-02", false),
        new("ap-lobby", true),
        new("core-sw-01", false),
        new("edge-fw", true),
    };

    [Fact]
    public void Shows_everything_in_source_order_by_default()
    {
        var view = new FilteredView(Fleet());

        Assert.Equal(new[] { "core-sw-02", "ap-lobby", "core-sw-01", "edge-fw" }, view.OfItems<Item>().Select(i => i.Name));
        Assert.Equal(4, view.Count);
    }

    [Fact]
    public void Filters_and_sorts_by_several_keys()
    {
        var view = new FilteredView(Fleet())
        {
            Filter = o => ((Item)o).Name != "edge-fw",
            Sort = FilteredView.ThenBy(
                FilteredView.By<Item, bool>(i => i.Pinned, descending: true),
                FilteredView.By<Item, string>(i => i.Name, comparer: StringComparer.OrdinalIgnoreCase)),
        };

        Assert.Equal(new[] { "ap-lobby", "core-sw-01", "core-sw-02" }, view.OfItems<Item>().Select(i => i.Name));
    }

    [Fact]
    public void Follows_the_source_and_announces_a_reset()
    {
        var source = Fleet();
        var view = new FilteredView(source) { Filter = o => ((Item)o).Pinned };
        var resets = 0;
        view.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        source.Add(new Item("dist-sw", true));

        Assert.Equal(3, view.Count);
        Assert.Equal(1, resets);
    }

    [Fact]
    public void Refresh_reapplies_a_filter_whose_inputs_changed()
    {
        var search = "core";
        var view = new FilteredView(Fleet()) { Filter = o => ((Item)o).Name.Contains(search, StringComparison.Ordinal) };
        Assert.Equal(2, view.Count);

        search = "edge";
        view.Refresh();

        Assert.Equal("edge-fw", Assert.Single(view.OfItems<Item>()).Name);
    }

    [Fact]
    public void Sorting_is_stable_for_equal_keys()
    {
        var view = new FilteredView(Fleet()) { Sort = FilteredView.By<Item, bool>(i => i.Pinned, descending: true) };

        Assert.Equal(new[] { "ap-lobby", "edge-fw", "core-sw-02", "core-sw-01" }, view.OfItems<Item>().Select(i => i.Name));
    }
}