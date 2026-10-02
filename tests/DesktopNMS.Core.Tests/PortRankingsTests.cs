using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class PortRankingsTests
{
    private static readonly Port[] Ports =
    {
        Port(1, 1, inBytes: 1000, outBytes: 10),
        Port(2, 1, inBytes: 10, outBytes: 5000),
        Port(3, 2, inBytes: 3000, outBytes: 3000),
        Port(4, 2, inBytes: 0, outBytes: 0, inErrors: 2.5, outErrors: 0),
        Port(5, 3, inBytes: null, outBytes: null, inErrors: 0, outErrors: 7),
    };

    [Fact]
    public void Traffic_is_ranked_in_bits_per_second_by_total()
    {
        var top = PortRankings.TopTraffic(Ports, RankBy.Total, 10);

        Assert.Equal(new[] { 3, 2, 1 }, top.Select(p => p.Port.PortId));
        Assert.Equal(6000 * 8, top[0].Total);
    }

    [Theory]
    [InlineData(RankBy.In, new[] { 3, 1, 2 })]
    [InlineData(RankBy.Out, new[] { 2, 3, 1 })]
    public void Traffic_can_be_ranked_by_one_direction(RankBy by, int[] expected)
    {
        Assert.Equal(expected, PortRankings.TopTraffic(Ports, by, 10).Select(p => p.Port.PortId));
    }

    [Fact]
    public void Idle_ports_are_left_out_and_the_count_is_honoured()
    {
        var top = PortRankings.TopTraffic(Ports, RankBy.Total, 2);

        Assert.Equal(2, top.Count);
        Assert.DoesNotContain(top, p => p.Port.PortId is 4 or 5);
    }

    [Fact]
    public void Errors_are_ranked_and_error_free_ports_left_out_by_default()
    {
        var top = PortRankings.TopErrors(Ports, RankBy.Total, 10);

        Assert.Equal(new[] { 5, 4 }, top.Select(p => p.Port.PortId));
        Assert.Equal(7, top[0].Out);
    }

    [Fact]
    public void Errors_can_include_error_free_ports()
    {
        Assert.Equal(5, PortRankings.TopErrors(Ports, RankBy.In, 10, includeZero: true).Count);
    }

    [Fact]
    public void Devices_sum_their_ports_traffic()
    {
        var top = PortRankings.TopDevices(Ports, RankBy.Total, 10);

        // Device 1: 1,010 in + 5,010 out = 6,020 bytes/s, just ahead of device 2's 6,000.
        Assert.Equal(new[] { 1, 2 }, top.Select(d => d.DeviceId));
        Assert.Equal((1000 + 10) * 8, top[0].In);
        Assert.Equal(2, top[0].ActivePorts);
        Assert.Equal(1, top[1].ActivePorts);
    }

    [Fact]
    public void Negative_or_missing_rates_count_as_nothing()
    {
        var odd = new[] { Port(9, 9, inBytes: -50, outBytes: double.NaN) };

        Assert.Empty(PortRankings.TopTraffic(odd, RankBy.Total, 10));
        Assert.Empty(PortRankings.TopDevices(odd, RankBy.Total, 10));
    }

    private static Port Port(int id, int device, double? inBytes, double? outBytes, double inErrors = 0, double outErrors = 0) => new()
    {
        PortId = id,
        DeviceId = device,
        IfName = "port" + id,
        IfInOctetsRate = inBytes,
        IfOutOctetsRate = outBytes,
        IfInErrorsRate = inErrors,
        IfOutErrorsRate = outErrors,
    };
}
