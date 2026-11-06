using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Ports tab's cards as headings and rows (#572), which the view hosts in
/// a virtualising list: how many cards fit across, how the groups flatten into
/// rows, and finding a port's row for scrolling and Tab.
/// </summary>
public sealed class PortCardRowLayoutTests
{
    [Theory]
    [InlineData(1000, 3)]
    [InlineData(652, 2)]
    [InlineData(651, 1)]
    [InlineData(320, 1)]
    [InlineData(200, 1)]
    [InlineData(0, 1)]
    [InlineData(-50, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void As_many_cards_fit_across_as_keep_their_minimum_width_and_never_fewer_than_one(double width, int columns)
    {
        Assert.Equal(columns, PortCardRowLayout.ColumnsFor(width, 320, 12));
    }

    [Fact]
    public void Ungrouped_cards_make_rows_with_no_heading()
    {
        var groups = Groups(PortGrouping.None, 1, 2, 3, 4, 5);

        var rows = PortCardRowLayout.Build(groups, 2);

        Assert.All(rows, r => Assert.IsType<PortCardItemsRow>(r));
        Assert.Equal(new[] { new[] { 1, 2 }, new[] { 3, 4 }, new[] { 5 } }, rows.Select(Ids));
        Assert.All(rows, r => Assert.Equal(2, ((PortCardItemsRow)r).Columns));
        Assert.Equal(new[] { false, false, true }, rows.Select(r => ((PortCardItemsRow)r).EndsGroup));
    }

    [Fact]
    public void Each_group_is_its_heading_then_its_rows_in_the_groups_order()
    {
        var groups = StatusGroups();

        var rows = PortCardRowLayout.Build(groups, 2);

        Assert.Equal(
            new[] { "Down", "cards 2,4", "cards 6", "Up", "cards 1,3", "cards 5" },
            rows.Select(Describe));
        Assert.Equal("3 ports", ((PortCardHeaderRow)rows[0]).CountText);
        Assert.Same(groups[1], ((PortCardHeaderRow)rows[3]).Group);
        Assert.Equal(new[] { false, true, false, true }, rows.OfType<PortCardItemsRow>().Select(r => r.EndsGroup));
    }

    [Fact]
    public void A_group_with_fewer_cards_than_columns_is_one_short_row_as_wide_as_a_full_one()
    {
        var rows = PortCardRowLayout.Build(Groups(PortGrouping.None, 1, 2), 4);

        var row = Assert.IsType<PortCardItemsRow>(Assert.Single(rows));
        Assert.Equal(2, row.Cards.Count);
        Assert.Equal(4, row.Columns);
        Assert.True(row.EndsGroup);
    }

    [Fact]
    public void One_column_puts_each_card_on_its_own_row()
    {
        var rows = PortCardRowLayout.Build(Groups(PortGrouping.None, 1, 2, 3), 1);

        Assert.Equal(new[] { new[] { 1 }, new[] { 2 }, new[] { 3 } }, rows.Select(Ids));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Fewer_than_one_column_is_taken_as_one(int columns)
    {
        var rows = PortCardRowLayout.Build(Groups(PortGrouping.None, 1, 2), columns);

        Assert.Equal(new[] { new[] { 1 }, new[] { 2 } }, rows.Select(Ids));
        Assert.All(rows, r => Assert.Equal(1, ((PortCardItemsRow)r).Columns));
    }

    [Fact]
    public void No_groups_make_no_rows()
    {
        Assert.Empty(PortCardRowLayout.Build(Array.Empty<PortCardGroup>(), 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Every_card_appears_once_in_the_groups_order(int columns)
    {
        var groups = StatusGroups();

        var rows = PortCardRowLayout.Build(groups, columns);

        var inRows = rows.OfType<PortCardItemsRow>().SelectMany(r => r.Cards).ToList();
        Assert.Equal(groups.SelectMany(g => g.Cards), inRows);
        Assert.Equal(inRows.Count, inRows.Distinct().Count());
        Assert.All(rows.OfType<PortCardItemsRow>(), r => Assert.InRange(r.Cards.Count, 1, columns));
    }

    [Fact]
    public void Fewer_columns_regroup_the_same_cards_into_more_rows()
    {
        var groups = StatusGroups();

        var wide = PortCardRowLayout.Build(groups, 3);
        var narrow = PortCardRowLayout.Build(groups, 1);

        Assert.Equal(new[] { "Down", "cards 2,4,6", "Up", "cards 1,3,5" }, wide.Select(Describe));
        Assert.Equal(8, narrow.Count);
        Assert.Equal(
            wide.OfType<PortCardItemsRow>().SelectMany(r => r.Cards),
            narrow.OfType<PortCardItemsRow>().SelectMany(r => r.Cards));
    }

    [Fact]
    public void A_picked_port_is_found_in_its_row_counting_the_headings()
    {
        var groups = StatusGroups();
        var rows = PortCardRowLayout.Build(groups, 2);
        var port5 = PortOf(groups, 5);
        var port6 = PortOf(groups, 6);

        Assert.Equal(5, PortCardRowLayout.RowOf(rows, port5));
        Assert.Equal(2, PortCardRowLayout.RowOf(rows, port6));
        Assert.Equal(3, PortCardRowLayout.RowOf(PortCardRowLayout.Build(groups, 1), port6));
    }

    [Fact]
    public void A_port_without_a_card_has_no_row()
    {
        var rows = PortCardRowLayout.Build(StatusGroups(), 2);

        Assert.Equal(-1, PortCardRowLayout.RowOf(rows, Item(Up(99))));
        Assert.Equal(-1, PortCardRowLayout.RowOf(Array.Empty<PortCardRow>(), Item(Up(1))));
    }

    [Fact]
    public void Tab_moves_through_the_cards_in_reading_order_across_rows_and_groups()
    {
        var groups = StatusGroups();
        var rows = PortCardRowLayout.Build(groups, 2);

        var order = new List<int>();
        var position = PortCardRowLayout.First(rows);
        while (position is { } at)
        {
            order.Add(at.Card.Port.Model.PortId);
            Assert.Equal(at.Row, PortCardRowLayout.RowOf(rows, at.Card.Port));
            position = PortCardRowLayout.Next(rows, at.Card.Port, forward: true);
        }

        Assert.Equal(new[] { 2, 4, 6, 1, 3, 5 }, order);
    }

    [Fact]
    public void Shift_tab_moves_back_and_both_ends_let_focus_leave()
    {
        var groups = StatusGroups();
        var rows = PortCardRowLayout.Build(groups, 2);

        var last = PortCardRowLayout.Last(rows);
        Assert.Equal(5, last?.Card.Port.Model.PortId);
        Assert.Equal(5, last?.Row);

        // From the first Up card back over the Up heading to Down's last card.
        var back = PortCardRowLayout.Next(rows, PortOf(groups, 1), forward: false);
        Assert.Equal(6, back?.Card.Port.Model.PortId);
        Assert.Equal(2, back?.Row);

        Assert.Null(PortCardRowLayout.Next(rows, PortOf(groups, 2), forward: false));
        Assert.Null(PortCardRowLayout.Next(rows, PortOf(groups, 5), forward: true));
        Assert.Null(PortCardRowLayout.Next(rows, Item(Up(99)), forward: true));
        Assert.Null(PortCardRowLayout.First(Array.Empty<PortCardRow>()));
        Assert.Null(PortCardRowLayout.Last(Array.Empty<PortCardRow>()));
    }

    /// <summary>Ports 1-6, odd ones up and even ones down, grouped by status: Down (2, 4, 6) then Up (1, 3, 5).</summary>
    private static IReadOnlyList<PortCardGroup> StatusGroups()
    {
        var ports = Enumerable.Range(1, 6).Select(id =>
        {
            var raw = Up(id);
            if (id % 2 == 0)
            {
                raw.IfOperStatus = "down";
            }

            return Item(raw);
        });

        var groups = PortCardGrouping.Build(ports, PortGrouping.Status, _ => { });
        Assert.Equal(new[] { "Down", "Up" }, groups.Select(g => g.Header));
        return groups;
    }

    private static IReadOnlyList<PortCardGroup> Groups(PortGrouping grouping, params int[] ids) =>
        PortCardGrouping.Build(ids.Select(id => Item(Up(id))), grouping, _ => { });

    private static PortItemViewModel PortOf(IReadOnlyList<PortCardGroup> groups, int id) =>
        groups.SelectMany(g => g.Cards).Single(c => c.Port.Model.PortId == id).Port;

    private static int[] Ids(PortCardRow row) => ((PortCardItemsRow)row).Cards.Select(c => c.Port.Model.PortId).ToArray();

    private static string Describe(PortCardRow row) => row switch
    {
        PortCardHeaderRow header => header.Header ?? "(none)",
        PortCardItemsRow items => "cards " + string.Join(",", items.Cards.Select(c => c.Port.Model.PortId)),
        _ => "?",
    };

    private static Port Up(int id) => new()
    {
        PortId = id,
        DeviceId = 7,
        IfName = "Gi0/" + id,
        IfDescr = "Gi0/" + id,
        IfOperStatus = "up",
        IfAdminStatus = "up",
        IfSpeed = 1_000_000_000,
        IfInErrorsDelta = 0,
        IfOutErrorsDelta = 0,
    };

    private static PortItemViewModel Item(Port port) =>
        new(port, null, Array.Empty<DeviceIpAddress>(), _ => { }, null, false);
}
