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

/// <summary>The port graphs - read by hand from LibreNMS's port graph definitions.</summary>
public class PortGraphSeriesNamesTests
{
    [Fact]
    public void Port_errors_name_errors_and_discards_each_way()
    {
        var names = GraphSeriesNames.Resolve("port_errors", ["#FF3300", "#FF6633", "#805080", "#C0A060"]);

        Assert.Equal(new[] { "Errors in", "Errors out", "Discards in", "Discards out" }, names);
    }

    [Fact]
    public void A_traffic_graph_with_an_extra_entry_keeps_librenms_legend()
        => Assert.Null(GraphSeriesNames.Resolve("port_bits", ["#608720", "#606090", "#FF0000"]));

    [Fact]
    public void A_port_has_values_where_librenms_reports_its_rates()
    {
        var port = new DesktopNMS.Core.Models.Port { IfInOctetsRate = 400_000_000, IfOutOctetsRate = 125_000, IfInErrorsRate = 0.02 };

        var traffic = GraphLegend.ForPort("port_bits", ["In", "Out"], port);
        var errors = GraphLegend.ForPort("port_errors", ["Errors in", "Errors out", "Discards in", "Discards out"], port);

        Assert.Equal(new[] { "3.2 Gb/s", "1 Mb/s" }, traffic.Select(e => e.Value));
        Assert.Equal(new[] { "0.02/s", null, null, null }, errors.Select(e => e.Value));
    }

    [Theory]
    [InlineData(0.02, "0.02/s")]
    [InlineData(840, "840/s")]
    [InlineData(1234, "1.2k/s")]
    public void Per_second_values_read_short(double value, string expected)
        => Assert.Equal(expected, GraphLegend.FormatPerSecond(value));
}
