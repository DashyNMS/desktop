using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AccessPointsTests
{
    private static NetworkLink L(string? name, string? version, int device = 1, int port = 10, string? remotePort = "00 4E 35 C5 7B 58 (004e35c57b58)") =>
        new() { RemoteHostname = name, RemoteVersion = version, LocalDeviceId = device, LocalPortId = port, RemotePort = remotePort };

    [Fact]
    public void Aruba_APs_are_recognised_by_what_they_announce()
    {
        var aps = AccessPoints.FromLinks(new[]
        {
            L("r-ap-it-01", "ArubaOS (MODEL: 535), Version Aruba AP"),
            L("r-sw-core-01", "Aruba S0E91A FL.10.13.1130"),
            L("r-wlc-ccc1-01", "Model:Aruba7008 Aruba Networks ArubaOS Version 8.10.0.14 LSR"),
            L("some-server", null),
        });

        var ap = Assert.Single(aps);
        Assert.Equal("r-ap-it-01", ap.Name);
        Assert.Equal("AP-535", ap.Model);
        Assert.Equal("00:4E:35:C5:7B:58", ap.Mac);
        Assert.Equal(1, ap.SwitchDeviceId);
        Assert.Equal(10, ap.SwitchPortId);
        Assert.False(ap.IsUnnamed);
    }

    [Fact]
    public void An_AP_without_a_name_is_named_by_its_MAC_and_listed_last()
    {
        const string version = "ArubaOS (MODEL: 535), Version Aruba AP";
        var aps = AccessPoints.FromLinks(new[] { L(version, version), L("r-ap-zz-01", version) });

        Assert.Equal(new[] { "r-ap-zz-01", "AP 00:4E:35:C5:7B:58" }, aps.Select(a => a.Name));
        Assert.True(aps[1].IsUnnamed);
    }

    [Fact]
    public void An_AP_on_two_ports_is_listed_on_both()
    {
        const string version = "ArubaOS (MODEL: 345), Version Aruba AP";
        var aps = AccessPoints.FromLinks(new[] { L("r-ap-fer-pdc-02", version, 178, 6730), L("r-ap-fer-pdc-02", version, 135, 4588) });

        Assert.Equal(2, aps.Count);
    }

    [Theory]
    [InlineData("ArubaOS (MODEL: 345), Version Aruba AP", "AP-345")]
    [InlineData("ArubaOS (MODEL: AP-567), Version Aruba AP", "AP-567")]
    [InlineData("ArubaOS, Version Aruba AP", null)]
    public void The_model_comes_from_the_announced_version(string version, string? model)
    {
        Assert.Equal(model, AccessPoints.ModelOf(version));
    }

    [Fact]
    public void A_link_parses_its_version_and_whether_it_is_active()
    {
        const string json = """{"id":1,"local_port_id":6730,"local_device_id":178,"remote_port_id":0,"active":0,"protocol":"lldp","remote_hostname":"r-ap-fer-pdc-02","remote_device_id":0,"remote_port":"00 4E 35 C5 7B 58 (004e35c57b58)","remote_platform":"","remote_version":"ArubaOS (MODEL: 345), Version Aruba AP"}""";

        var link = JsonSerializer.Deserialize<NetworkLink>(json, LibreNmsJson.Options)!;

        Assert.False(link.Active);
        Assert.True(AccessPoints.IsAccessPoint(link));
    }
}
