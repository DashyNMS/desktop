using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusDeviceMatcherTests
{
    private static UnimusDevice Device(int id, string? address, string? description) =>
        new() { Id = id, Address = address, Description = description };

    [Fact]
    public void Matches_by_address_when_no_hostname_candidate_matches()
    {
        var devices = new[] { Device(2, "192.0.2.33", "sw-desk-02") };

        var match = UnimusDeviceMatcher.Match(devices, new[] { "switch1", "192.0.2.33" });

        Assert.NotNull(match);
        Assert.Equal(2, match!.Id);
    }

    [Fact]
    public void Matches_by_description_the_field_findByAddress_cannot_reach()
    {
        // The real bug this exists to work around: Unimus's own
        // findByAddress endpoint never matches on description (its
        // hostname-ish field) at all - only Address. Confirmed live.
        var devices = new[] { Device(5, "192.0.2.48", "sw-aud-01.corp.example.net") };

        var match = UnimusDeviceMatcher.Match(devices, new[] { "sw-aud-01.corp.example.net", "10.0.0.1" });

        Assert.NotNull(match);
        Assert.Equal(5, match!.Id);
    }

    [Fact]
    public void Earlier_candidates_win_over_later_ones()
    {
        var devices = new[]
        {
            Device(1, "10.0.0.1", "switch1-by-description"),
            Device(2, "10.0.0.2", "switch1"),
        };

        // "switch1" (candidate order: hostname first) should match device 2
        // by description before the IP candidate is ever considered.
        var match = UnimusDeviceMatcher.Match(devices, new[] { "switch1", "10.0.0.1" });

        Assert.Equal(2, match!.Id);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var devices = new[] { Device(1, null, "Switch1.Corp.Example.Com") };

        var match = UnimusDeviceMatcher.Match(devices, new[] { "switch1.corp.example.com" });

        Assert.NotNull(match);
    }

    [Fact]
    public void No_match_returns_null_rather_than_throwing()
    {
        var devices = new[] { Device(1, "10.0.0.1", "switch1") };

        var match = UnimusDeviceMatcher.Match(devices, new[] { "switch2", "10.0.0.2" });

        Assert.Null(match);
    }

    [Fact]
    public void An_empty_device_list_is_handled()
    {
        var match = UnimusDeviceMatcher.Match(Array.Empty<UnimusDevice>(), new[] { "switch1" });

        Assert.Null(match);
    }

    [Fact]
    public void An_empty_candidate_list_is_handled()
    {
        var devices = new[] { Device(1, "10.0.0.1", "switch1") };

        var match = UnimusDeviceMatcher.Match(devices, Array.Empty<string>());

        Assert.Null(match);
    }

    [Fact]
    public void A_device_with_neither_field_set_never_matches()
    {
        var devices = new[] { Device(1, null, null) };

        var match = UnimusDeviceMatcher.Match(devices, new[] { "switch1", "" });

        Assert.Null(match);
    }
}
