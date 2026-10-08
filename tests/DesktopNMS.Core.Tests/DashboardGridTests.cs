using DesktopNMS.Core.Configuration;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class DashboardGridTests
{
    private static GridItem Item(string id, int column, int row, int columnSpan = 10, int rowSpan = 7) => new(id, column, row, columnSpan, rowSpan);

    [Fact]
    public void A_new_widget_goes_beside_the_first_rather_than_under_it()
        => Assert.Equal((10, 0), DashboardGrid.FindFreeSpot([Item("a", 0, 0)], 10, 7));

    [Fact]
    public void A_full_row_sends_the_next_widget_down()
        => Assert.Equal((0, 7), DashboardGrid.FindFreeSpot([Item("a", 0, 0), Item("b", 10, 0), Item("c", 20, 0)], 10, 7));

    [Fact]
    public void Moving_onto_a_widget_pushes_it_and_whatever_it_lands_on_down()
    {
        var moved = Item("moved", 0, 0);
        var b = Item("b", 0, 5);
        var c = Item("c", 0, 12);
        GridItem[] all = [moved, b, c];

        DashboardGrid.PushDown(moved, all);

        Assert.Equal(7, b.Row);
        Assert.Equal(14, c.Row);
        Assert.Equal(0, moved.Row);
    }

    [Fact]
    public void Tidy_up_closes_gaps_keeping_order_and_columns()
    {
        var a = Item("a", 0, 3);
        var b = Item("b", 0, 20);
        var c = Item("c", 10, 9);
        GridItem[] all = [a, b, c];

        DashboardGrid.TidyUp(all);

        Assert.Equal((0, 0), (a.Column, a.Row));
        Assert.Equal((0, 7), (b.Column, b.Row));
        Assert.Equal((10, 0), (c.Column, c.Row));
    }

    [Theory]
    [InlineData(WidgetSize.Small, DashboardWidget.MinColumnSpan, DashboardWidget.MinRowSpan)]
    [InlineData(WidgetSize.Medium, DashboardWidget.DefaultColumnSpan, DashboardWidget.DefaultRowSpan)]
    [InlineData(WidgetSize.Large, 15, 10)]
    [InlineData(WidgetSize.Wide, 20, 6)]
    public void Sizes_have_their_spans_and_are_recognised(WidgetSize size, int columns, int rows)
    {
        Assert.Equal((columns, rows), DashboardGrid.SpanFor(size));
        Assert.Equal(size, DashboardGrid.SizeOf(columns, rows));
    }

    [Fact]
    public void A_size_nothing_matches_is_none()
        => Assert.Null(DashboardGrid.SizeOf(11, 7));

    [Fact]
    public void Guides_show_where_edges_line_up()
    {
        var moving = Item("m", 10, 7);
        var (columns, rows) = DashboardGrid.AlignedEdges(moving, [Item("a", 0, 0), Item("b", 20, 7, 10, 3)]);

        // a's right edge meets m's left (10); b's left meets m's right (20); a's bottom and b's top meet m's top (7).
        Assert.Equal(new[] { 10, 20 }, columns);
        Assert.Equal(new[] { 7 }, rows);
    }
}
