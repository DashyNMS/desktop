using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceNameStyleTests
{
    private static Device Sample => new()
    {
        DeviceId = 12,
        Hostname = "192.0.2.10",
        SysName = "sw-core-09",
        Display = "Core switch 9",
    };

    [Theory]
    [InlineData(DeviceNameStyle.Hostname, "192.0.2.10")]
    [InlineData(DeviceNameStyle.SysName, "sw-core-09")]
    [InlineData(DeviceNameStyle.DisplayName, "Core switch 9")]
    public void Each_style_picks_its_field(DeviceNameStyle style, string expected)
        => Assert.Equal(expected, style.Resolve(Sample, "192.0.2.10"));

    [Fact]
    public void SysName_falls_back_to_hostname_when_empty()
    {
        var device = new Device { DeviceId = 12, Hostname = "192.0.2.10", SysName = "" };

        Assert.Equal("192.0.2.10", DeviceNameStyle.SysName.Resolve(device, "192.0.2.10"));
    }

    [Fact]
    public void Display_falls_back_through_sysName_to_hostname()
    {
        var device = new Device { DeviceId = 12, Hostname = "192.0.2.10", SysName = "sw-core-09" };

        Assert.Equal("sw-core-09", DeviceNameStyle.DisplayName.Resolve(device, "192.0.2.10"));
    }

    [Fact]
    public void Uncached_device_falls_back_to_the_hostname_from_the_alert()
    {
        // The device list loads in the background, so a name must still render
        // on the very first poll.
        Assert.Equal("192.0.2.10", DeviceNameStyle.SysName.Resolve(null, "192.0.2.10"));
        Assert.Equal("192.0.2.10", DeviceNameStyle.DisplayName.Resolve(null, "192.0.2.10"));
    }

    [Fact]
    public void The_name_never_comes_back_empty()
        => Assert.False(string.IsNullOrWhiteSpace(DeviceNameStyle.SysName.Resolve(null, null)));

    [Fact]
    public void Secondary_shows_the_other_name()
    {
        Assert.Equal("sw-core-09", DeviceNameStyle.Hostname.ResolveSecondary(Sample, "192.0.2.10", "192.0.2.10"));
        Assert.Equal("192.0.2.10", DeviceNameStyle.SysName.ResolveSecondary(Sample, "192.0.2.10", "sw-core-09"));
    }

    [Fact]
    public void Secondary_is_suppressed_when_it_would_repeat_the_primary()
    {
        var device = new Device { DeviceId = 12, Hostname = "sw-01", SysName = "sw-01" };

        Assert.Null(DeviceNameStyle.Hostname.ResolveSecondary(device, "sw-01", "sw-01"));
        Assert.Null(DeviceNameStyle.SysName.ResolveSecondary(null, "sw-01", "sw-01"));
    }
}
