using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;

namespace DesktopNMS.Services;

/// <summary>Every switch neighbour LibreNMS's links list has, with its switch port's current state - see <see cref="Neighbours"/>.</summary>
public sealed record NeighbourSnapshot(
    IReadOnlyList<Neighbour> Neighbours,
    IReadOnlyDictionary<int, Port> Ports,
    DateTimeOffset LoadedAt)
{
    public Port? PortOf(Neighbour neighbour) => Ports.GetValueOrDefault(neighbour.SwitchPortId);

    /// <summary>The neighbours a view lists - its rules, tested with each one's switch and switch port as well.</summary>
    public IReadOnlyList<Neighbour> For(NeighbourViewDefinition view, Func<int, string?> switchName) =>
        Neighbours.Where(n => Core.Topology.Neighbours.Matches(view, n, switchName(n.SwitchDeviceId), PortOf(n)?.IfAlias)).ToList();
}

/// <summary>
/// The fleet's switch neighbours, shared by the Neighbours tab, its view
/// editor's preview and the network map. Building the list takes two
/// fleet-wide calls (every link, every port - a couple of seconds), so a
/// result is kept for a short while and callers asking at the same time
/// share one fetch.
/// </summary>
public interface INeighbourDirectory
{
    /// <param name="refresh">Fetch afresh even if a recent result is to hand - a user's refresh.</param>
    Task<NeighbourSnapshot> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class NeighbourDirectory : INeighbourDirectory
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _devices;
    private readonly object _gate = new();

    private NeighbourSnapshot? _latest;
    private Task<NeighbourSnapshot>? _inFlight;

    public NeighbourDirectory(ILibreNmsClient client, IDeviceCache devices)
    {
        _client = client;
        _devices = devices;
    }

    public Task<NeighbourSnapshot> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!refresh && _latest is { } latest && DateTimeOffset.Now - latest.LoadedAt < FreshFor)
            {
                return Task.FromResult(latest);
            }

            if (_inFlight is { IsCompleted: false } running)
            {
                return running;
            }

            _inFlight = LoadAsync();
            return _inFlight;
        }
    }

    private async Task<NeighbourSnapshot> LoadAsync()
    {
        // Not cancelled with any one caller - another may be sharing it.
        var linksTask = _client.Links.ListAllAsync();
        var portsTask = _client.Ports.ListAllStatusAsync();
        await Task.WhenAll(linksTask, portsTask).ConfigureAwait(false);

        var neighbours = Neighbours.FromLinks(linksTask.Result);
        var ports = portsTask.Result
            .GroupBy(p => p.PortId)
            .ToDictionary(g => g.Key, g => g.First());

        // Switch names (which rules can test) and the state of neighbours
        // that are LibreNMS devices come from the device cache - make sure
        // it has them.
        var deviceIds = neighbours.Select(n => n.SwitchDeviceId)
            .Concat(neighbours.Where(n => n.IsMonitored).Select(n => n.RemoteDeviceId!.Value))
            .Distinct();
        await _devices.EnsureCurrentAsync(deviceIds).ConfigureAwait(false);

        var snapshot = new NeighbourSnapshot(neighbours, ports, DateTimeOffset.Now);
        lock (_gate)
        {
            _latest = snapshot;
        }

        return snapshot;
    }
}
