using DesktopNMS.Core.Graphs;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Against the generated table - LibreNMS's own graph definitions.</summary>
public class GraphSeriesNamesTests
{
    [Fact]
    public void Tcp_statistics_are_named_by_colour()
    {
        // netstat_tcp.inc.php: the "mixed" palette in order.
        var names = GraphSeriesNames.Resolve("device_netstat_tcp", ["#CC0000", "#008C00", "#4096EE"]);

        Assert.Equal(new[] { "InSegs", "OutSegs", "ActiveOpens" }, names);
    }

    [Fact]
    public void Ping_response_is_named_by_colour_whichever_averages_the_time_range_draws()
    {
        // generic_stats: the series, its percentiles and the 1 hour average on a day's graph - no 1 day or 1 week.
        var names = GraphSeriesNames.Resolve("device_ping_perf", ["#663399", "#3366BB", "#22CCBB", "#00BBCC", "#0099CC"]);

        Assert.Equal(new[] { "Milliseconds", "1 hour average", "25th percentile", "50th percentile", "75th percentile" }, names);
    }

    [Fact]
    public void A_wrapped_palette_colour_names_the_main_series()
    {
        Assert.Equal(new[] { "Uptime", "1 hour average" }, GraphSeriesNames.Resolve("device_uptime", ["#CAE853", "#49A81E"]));
    }

    [Theory]
    [InlineData("device_netstat_tcp", new[] { "#123456" })]
    [InlineData("device_not_a_graph", new[] { "#CC0000" })]
    public void Anything_it_can_not_be_sure_of_is_null(string graph, string[] colours)
        => Assert.Null(GraphSeriesNames.Resolve(graph, colours));
}
