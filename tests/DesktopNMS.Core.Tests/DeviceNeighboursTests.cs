using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class DeviceNeighboursTests
{
    private const int Me = 10;

    private static readonly Dictionary<int, string> MyPorts = new()
    {
        [101] = "Gi1/0/1",
        [124] = "Gi1/0/24",
    };

    private static readonly Dictionary<int, (int, NeighbourMatchKind)> NoMatches = new();

    private static NetworkLink Link(int id, int local, int localPort, int? remote, int? remotePort, string name, string port, string protocol = "lldp", bool active = true) => new()
    {
        Id = id,
        LocalDeviceId = local,
        LocalPortId = localPort,
        RemoteDeviceId = remote,
        RemotePortId = remotePort,
        RemoteHostname = name,
        RemotePort = port,
        Protocol = protocol,
        Active = active,
    };

    [Fact]
    public void This_devices_own_neighbours_are_named_from_its_ports()
    {
        var rows = DeviceNeighbours.Build(Me, new[] { Link(1, Me, 124, 20, 201, "dist-sw-01", "Te1/1/1") }, Array.Empty<NetworkLink>(), MyPorts, NoMatches);

        var row = Assert.Single(rows);
        Assert.Equal(NeighbourSource.ThisDevice, row.Source);
        Assert.Equal("Gi1/0/24", row.LocalPortName);
        Assert.Equal("Te1/1/1", row.RemotePortName);
        Assert.Equal(20, row.RemoteDeviceId);
        Assert.Equal(NeighbourMatchKind.LibreNms, row.Match);
    }

    [Fact]
    public void Lldp_and_cdp_for_the_same_neighbour_on_one_port_show_once()
    {
        var rows = DeviceNeighbours.Build(
            Me,
            new[] { Link(1, Me, 124, 20, 201, "dist-sw-01", "Te1/1/1"), Link(2, Me, 124, 20, 201, "dist-sw-01", "Te1/1/1", "cdp") },
            Array.Empty<NetworkLink>(),
            MyPorts,
            NoMatches);

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "CDP", "LLDP" }, row.Protocols);
    }

    [Fact]
    public void A_device_that_sees_this_one_shows_even_without_this_devices_own_lldp()
    {
        // An AP with no LLDP of its own: only its switch reports the connection.
        var fleet = new[] { Link(7, 30, 312, Me, 101, "ap-lobby-3", "eth0") };

        var row = Assert.Single(DeviceNeighbours.Build(Me, Array.Empty<NetworkLink>(), fleet, MyPorts, NoMatches));

        Assert.Equal(NeighbourSource.Neighbour, row.Source);
        Assert.Equal(30, row.RemoteDeviceId);
        Assert.Equal(312, row.RemotePortId);
        Assert.Equal("Gi1/0/1", row.LocalPortName);
    }

    [Fact]
    public void A_connection_both_sides_report_shows_once_from_this_side()
    {
        var own = new[] { Link(1, Me, 124, 20, 201, "dist-sw-01", "Te1/1/1") };
        var fleet = new[] { Link(9, 20, 201, Me, 124, "core-sw-01", "Gi1/0/24"), own[0] };

        var row = Assert.Single(DeviceNeighbours.Build(Me, own, fleet, MyPorts, NoMatches));

        Assert.Equal(NeighbourSource.ThisDevice, row.Source);
    }

    [Fact]
    public void A_neighbour_matched_by_name_carries_that_device()
    {
        var matches = new Dictionary<int, (int, NeighbourMatchKind)> { [1] = (44, NeighbourMatchKind.Name) };

        var row = Assert.Single(DeviceNeighbours.Build(Me, new[] { Link(1, Me, 124, null, null, "Edge FW 01", "port1") }, Array.Empty<NetworkLink>(), MyPorts, matches));

        Assert.Equal(44, row.RemoteDeviceId);
        Assert.Equal(NeighbourMatchKind.Name, row.Match);
    }
}
