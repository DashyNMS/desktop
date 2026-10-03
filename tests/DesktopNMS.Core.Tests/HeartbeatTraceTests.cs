using DesktopNMS.Core.Branding;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class HeartbeatTraceTests
{
    [Fact]
    public void A_sweep_draws_in_from_the_left_then_wipes_away_from_the_left()
    {
        var length = HeartbeatTrace.Length;

        Assert.Equal((0, 0), HeartbeatTrace.Visible(0));
        Assert.Equal(0, HeartbeatTrace.Visible(0.275).From);
        Assert.Equal(length / 2, HeartbeatTrace.Visible(0.275).To, precision: 6);
        Assert.Equal(length, HeartbeatTrace.Visible(0.55).To, precision: 6);
        Assert.Equal(length / 2, HeartbeatTrace.Visible(0.775).From, precision: 6);
        Assert.Equal(length, HeartbeatTrace.Visible(0.775).To, precision: 6);

        // And round again.
        Assert.Equal(HeartbeatTrace.Visible(0.3).To, HeartbeatTrace.Visible(1.3).To, precision: 9);
    }

    [Fact]
    public void The_whole_line_is_every_corner()
    {
        Assert.Equal(HeartbeatTrace.Points, HeartbeatTrace.Between(0, HeartbeatTrace.Length));
    }

    [Fact]
    public void Part_of_the_line_starts_and_ends_between_corners()
    {
        // The first flat run is 14 long (24 to 38): half of it, then on past the next corner.
        var part = HeartbeatTrace.Between(7, 20);

        Assert.Equal((31, 55), part[0]);
        Assert.Equal((38, 55), part[1]);
        Assert.Equal(3, part.Count);
        Assert.True(part[2].X is > 38 and < 45.5);
    }

    [Fact]
    public void Nothing_to_draw_is_no_points() =>
        Assert.Empty(HeartbeatTrace.Between(5, 5));
}
