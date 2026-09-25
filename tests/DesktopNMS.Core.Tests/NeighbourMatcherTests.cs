using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class NeighbourMatcherTests
{
    private static readonly Device[] Devices =
    {
        new() { DeviceId = 443, Hostname = "192.0.2.174", SysName = "00 stage left a", Display = "00 stage left a" },
        new() { DeviceId = 315, Hostname = "10.45.69.101", SysName = "rack", Display = "g-bol-101" },
        new() { DeviceId = 17, Hostname = "192.0.2.5", SysName = "ws-lab-05", Display = "ws-lab-05" },
        new() { DeviceId = 368, Hostname = "192.0.2.101", SysName = "studio_02" },
        new() { DeviceId = 332, Hostname = "192.0.2.104", SysName = "studio_02" },
    };

    [Theory]
    [InlineData("00 Stage Left (A)", 443)]
    [InlineData("Rack", 315)]
    [InlineData("G-BOL-101", 315)]
    [InlineData("ws-lab-05.corp.example.net", 17)]
    [InlineData("10.45.69.101", 315)]
    public void Names_match_ignoring_case_punctuation_and_domain(string announced, int expected)
    {
        Assert.Equal(expected, NeighbourMatcher.MatchByName(announced, Devices));
    }

    [Theory]
    [InlineData("studio_02")]           // two devices share it
    [InlineData("Intercom-128-08-AA-AD")] // nothing like it
    [InlineData("ab")]                   // too short to trust
    [InlineData(null)]
    [InlineData("  ")]
    public void No_match_or_an_ambiguous_one_links_nothing(string? announced)
    {
        Assert.Null(NeighbourMatcher.MatchByName(announced, Devices));
    }

    [Fact]
    public void An_IP_is_not_shortened_to_its_first_octet()
    {
        // "10" would be too short anyway, but "192.0.2.1" mustn't become "10".
        Assert.Null(NeighbourMatcher.MatchByName("10.99.99.99", Devices));
    }

    [Theory]
    [InlineData("00 19 7C 02 E8 8B (00197c02e88b)", "00197c02e88b")]
    [InlineData("00:19:7c:02:e8:8b", "00197c02e88b")]
    [InlineData("Gi1/0/48", null)]
    [InlineData(null, null)]
    public void A_MAC_is_read_out_of_an_announced_port_id(string? port, string? expected)
    {
        Assert.Equal(expected, NeighbourMatcher.MacFromPortId(port));
    }
}
