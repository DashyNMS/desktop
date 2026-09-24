using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class NeighbourMatcherTests
{
    private static readonly Device[] Devices =
    {
        new() { DeviceId = 443, Hostname = "10.46.69.174", SysName = "00 red flag alp", Display = "00 red flag alp" },
        new() { DeviceId = 315, Hostname = "10.45.69.101", SysName = "rack", Display = "g-bol-101" },
        new() { DeviceId = 17, Hostname = "10.44.10.5", SysName = "g-ws-roc-05", Display = "g-ws-roc-05" },
        new() { DeviceId = 368, Hostname = "10.46.69.101", SysName = "paddock_02" },
        new() { DeviceId = 332, Hostname = "10.46.69.104", SysName = "paddock_02" },
    };

    [Theory]
    [InlineData("00 Red Flag (ALP)", 443)]
    [InlineData("Rack", 315)]
    [InlineData("G-BOL-101", 315)]
    [InlineData("g-ws-roc-05.fia.riedel.local", 17)]
    [InlineData("10.45.69.101", 315)]
    public void Names_match_ignoring_case_punctuation_and_domain(string announced, int expected)
    {
        Assert.Equal(expected, NeighbourMatcher.MatchByName(announced, Devices));
    }

    [Theory]
    [InlineData("paddock_02")]           // two devices share it
    [InlineData("Riedel-UIC-128-08-AA-AD")] // nothing like it
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
        // "10" would be too short anyway, but "10.46.69.1" mustn't become "10".
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
