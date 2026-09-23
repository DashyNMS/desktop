using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class NetworkTopologyTests
{
    private static NetworkLink Link(int localDevice, int localPort, int? remoteDevice, int? remotePort, string? remotePortName = null) => new()
    {
        LocalDeviceId = localDevice,
        LocalPortId = localPort,
        RemoteDeviceId = remoteDevice,
        RemotePortId = remotePort,
        RemotePort = remotePortName,
    };

    [Fact]
    public void A_cable_reported_from_both_ends_is_one_link_on_one_edge()
    {
        var links = new[]
        {
            Link(1, 101, 2, 201, "Gi1/0/48"),
            Link(2, 201, 1, 101, "1/1/1"),
        };

        var graph = NetworkTopology.Build(new[] { 1, 2 }, links);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal((1, 2), (edge.DeviceA, edge.DeviceB));
        Assert.Equal(1, edge.LinkCount);

        // Each end's port name comes from the record discovered on the other end.
        var connection = Assert.Single(edge.Connections);
        Assert.Equal("1/1/1", connection.PortA);
        Assert.Equal("Gi1/0/48", connection.PortB);
    }

    [Fact]
    public void A_cable_reported_from_one_end_only_names_just_the_far_port()
    {
        var edge = Assert.Single(NetworkTopology.Build(new[] { 1, 2 }, new[] { Link(2, 201, 1, 101, "1/1/1") }).Edges);

        var connection = Assert.Single(edge.Connections);
        Assert.Equal("1/1/1", connection.PortA);
        Assert.Null(connection.PortB);
    }

    [Fact]
    public void Several_cables_between_the_same_pair_share_one_edge()
    {
        var links = new[]
        {
            Link(1, 101, 2, 201),
            Link(1, 102, 2, 202),
            Link(2, 201, 1, 101),
            Link(2, 202, 1, 102),
        };

        var edge = Assert.Single(NetworkTopology.Build(new[] { 1, 2 }, links).Edges);

        Assert.Equal(2, edge.LinkCount);
    }

    [Fact]
    public void Unmonitored_neighbours_and_self_links_are_left_out()
    {
        var links = new[]
        {
            Link(1, 101, null, null, "SEP001122334455"),
            Link(1, 102, 1, 103),
        };

        var graph = NetworkTopology.Build(new[] { 1 }, links);

        Assert.Empty(graph.Edges);
        Assert.Equal(new[] { 1 }, graph.UnlinkedDeviceIds);
    }

    [Fact]
    public void Links_leaving_the_scope_are_ignored()
    {
        // Device 3 isn't in this group, so its link to 1 doesn't appear and
        // 1 counts as unlinked within the group.
        var links = new[] { Link(1, 101, 3, 301), Link(2, 201, 3, 302) };

        var graph = NetworkTopology.Build(new[] { 1, 2 }, links);

        Assert.Empty(graph.Edges);
        Assert.Equal(new[] { 1, 2 }, graph.UnlinkedDeviceIds);
    }

    [Fact]
    public void Remote_port_name_stands_in_when_there_is_no_remote_port_id()
    {
        var links = new[]
        {
            Link(1, 101, 2, null, "Gi1/0/1"),
            Link(1, 101, 2, null, "Gi1/0/1"),
        };

        Assert.Equal(1, Assert.Single(NetworkTopology.Build(new[] { 1, 2 }, links).Edges).LinkCount);
    }
}

public class ForceDirectedLayoutTests
{
    private static double Distance(MapPoint a, MapPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    [Fact]
    public void Layout_is_deterministic()
    {
        var nodes = Enumerable.Range(1, 12).ToList();
        var edges = nodes.Skip(1).Select(n => (1, n)).ToList();

        var first = ForceDirectedLayout.Compute(nodes, edges, new Dictionary<int, MapPoint>());
        var second = ForceDirectedLayout.Compute(nodes, edges, new Dictionary<int, MapPoint>());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Pinned_nodes_do_not_move()
    {
        var pinned = new Dictionary<int, MapPoint> { [1] = new(500, -200) };

        var layout = ForceDirectedLayout.Compute(new[] { 1, 2, 3 }, new[] { (1, 2), (2, 3) }, pinned);

        Assert.Equal(new MapPoint(500, -200), layout[1]);
    }

    [Fact]
    public void A_new_node_lands_near_its_pinned_neighbour()
    {
        var pinned = new Dictionary<int, MapPoint> { [1] = new(1000, 1000), [2] = new(-1000, -1000) };

        var layout = ForceDirectedLayout.Compute(new[] { 1, 2, 3 }, new[] { (1, 3) }, pinned);

        Assert.True(Distance(layout[3], layout[1]) < Distance(layout[3], layout[2]));
    }

    [Fact]
    public void Connected_nodes_end_up_closer_than_unconnected_ones()
    {
        // Two separate triangles: within-triangle distances should be
        // shorter on average than distances across them.
        var nodes = new[] { 1, 2, 3, 4, 5, 6 };
        var edges = new[] { (1, 2), (2, 3), (1, 3), (4, 5), (5, 6), (4, 6) };

        var layout = ForceDirectedLayout.Compute(nodes, edges, new Dictionary<int, MapPoint>());

        var within = edges.Average(e => Distance(layout[e.Item1], layout[e.Item2]));
        var across = new[] { (1, 4), (2, 5), (3, 6) }.Average(e => Distance(layout[e.Item1], layout[e.Item2]));
        Assert.True(within < across, $"within {within:F0} should be < across {across:F0}");
        Assert.All(layout.Values, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
    }

    [Fact]
    public void Grid_places_unlinked_nodes_below_the_existing_layout()
    {
        var existing = new[] { new MapPoint(0, 0), new MapPoint(300, 200) };

        var grid = ForceDirectedLayout.Grid(new[] { 7, 8, 9 }, existing);

        Assert.All(grid.Values, p => Assert.True(p.Y > 200));
        Assert.Equal(3, grid.Values.Distinct().Count());
    }
}

public class MapLayoutStoreTests
{
    [Fact]
    public void Positions_round_trip_through_the_file_per_scope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"map-layouts-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MapLayoutStore(path, NullLogger<MapLayoutStore>.Instance);
            store.Save("server|all", new Dictionary<int, MapPoint> { [1] = new(10.04, -5), [2] = new(3, 4) });
            store.Save("server|group:Core", new Dictionary<int, MapPoint> { [1] = new(99, 99) });

            var reloaded = new MapLayoutStore(path, NullLogger<MapLayoutStore>.Instance);

            Assert.Equal(new MapPoint(10, -5), reloaded.Get("server|all")[1]);
            Assert.Equal(new MapPoint(99, 99), reloaded.Get("server|group:Core")[1]);

            reloaded.Clear("server|all");
            Assert.Empty(new MapLayoutStore(path, NullLogger<MapLayoutStore>.Instance).Get("server|all"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_corrupt_file_just_means_no_saved_layout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"map-layouts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Empty(new MapLayoutStore(path, NullLogger<MapLayoutStore>.Instance).Get("any"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
