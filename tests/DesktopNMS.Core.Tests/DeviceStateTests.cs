using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceStateTests
{
    [Fact]
    public void Up_when_status_true_and_not_disabled_or_ignored()
    {
        var device = new Device { Status = true };
        Assert.Equal(DeviceState.Up, device.State);
    }

    [Fact]
    public void Down_when_status_false_and_not_disabled_or_ignored()
    {
        var device = new Device { Status = false };
        Assert.Equal(DeviceState.Down, device.State);
    }

    [Fact]
    public void Disabled_wins_even_if_status_says_up()
    {
        // A disabled device stops being polled, so its last-known status is
        // stale and must not be shown as if it still means something.
        var device = new Device { Status = true, Disabled = true };
        Assert.Equal(DeviceState.Disabled, device.State);
    }

    [Fact]
    public void Ignored_wins_over_status_but_not_over_disabled()
    {
        var ignored = new Device { Status = true, Ignore = true };
        Assert.Equal(DeviceState.Ignored, ignored.State);

        var both = new Device { Status = true, Disabled = true, Ignore = true };
        Assert.Equal(DeviceState.Disabled, both.State);
    }
}
