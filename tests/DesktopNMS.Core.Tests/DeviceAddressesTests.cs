using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Naming a Graylog message's source (#304).</summary>
public class DeviceAddressesTests
{
    private static readonly Device[] Devices =
    {
        new() { DeviceId = 1, Hostname = "core-sw-01.example.net", SysName = "core-sw-01", Ip = "192.0.2.1" },
        new() { DeviceId = 2, Hostname = "192.0.2.2", SysName = "dist-sw-02.example.net", Ip = "192.0.2.2" },
        new() { DeviceId = 3, Hostname = "ap-lobby.site-a.example.net", Ip = "192.0.2.3" },
        new() { DeviceId = 4, Hostname = "ap-lobby.site-b.example.net", Ip = "192.0.2.4" },
    };

    [Theory]
    [InlineData("192.0.2.1", 1)]
    [InlineData("core-sw-01.example.net", 1)]
    [InlineData("CORE-SW-01.EXAMPLE.NET", 1)]
    [InlineData("dist-sw-02.example.net", 2)]
    [InlineData(" 192.0.2.2 ", 2)]
    public void Matches_an_ip_hostname_or_sysname_ignoring_case(string address, int expected)
        => Assert.Equal(expected, DeviceAddresses.Find(Devices, address)?.DeviceId);

    [Theory]
    [InlineData("core-sw-01", 1)]
    [InlineData("Core-SW-01", 1)]
    [InlineData("dist-sw-02", 2)]
    [InlineData("core-sw-01.other.example", 1)]
    public void Matches_a_short_name_when_only_one_device_has_it(string address, int expected)
        => Assert.Equal(expected, DeviceAddresses.Find(Devices, address)?.DeviceId);

    [Fact]
    public void A_short_name_two_devices_share_matches_neither()
        => Assert.Null(DeviceAddresses.Find(Devices, "ap-lobby"));

    [Theory]
    [InlineData("192.0.2.99")]
    [InlineData("192.0.2")]
    [InlineData("unknown-host")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_matches_nothing(string? address)
        => Assert.Null(DeviceAddresses.Find(Devices, address));

    [Fact]
    public void An_exact_match_wins_over_a_short_name()
    {
        var devices = new[]
        {
            new Device { DeviceId = 7, Hostname = "edge.example.net" },
            new Device { DeviceId = 8, Hostname = "edge" },
        };

        Assert.Equal(8, DeviceAddresses.Find(devices, "edge")?.DeviceId);
    }
}
