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
    IReadOnlyDictionary<string, string> IpByMac,
    DateTimeOffset LoadedAt)
{
    public Port? PortOf(Neighbour neighbour) => Ports.GetValueOrDefault(neighbour.SwitchPortId);

    /// <summary>The IP the fleet's ARP tables have for the neighbour's MAC, if any.</summary>
    public string? IpOf(Neighbour neighbour) =>
        neighbour.Mac is { } mac && IpByMac.TryGetValue(HexOf(mac), out var ip) ? ip : null;

    internal static string HexOf(string mac) => new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

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
    private readonly IFleetLinks _links;
    private readonly object _gate = new();

    private NeighbourSnapshot? _latest;
    private Task<NeighbourSnapshot>? _inFlight;

    public NeighbourDirectory(ILibreNmsClient client, IDeviceCache devices, IFleetLinks links)
    {
        _client = client;
        _devices = devices;
        _links = links;
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

            _inFlight = LoadAsync(refresh);
            return _inFlight;
        }
    }

    /// <summary>
    /// MAC to IP from every ARP table LibreNMS has - for a neighbour it
    /// doesn't monitor, the only way to an IP (LLDP's management address
    /// isn't kept). Best effort: without it the IP column is just blank.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadIpByMacAsync()
    {
        try
        {
            var arp = await _client.Arp.ListAllAsync().ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in arp)
            {
                if (entry.MacAddress is { } mac && !string.IsNullOrWhiteSpace(entry.Ipv4Address))
                {
                    map.TryAdd(NeighbourSnapshot.HexOf(mac), entry.Ipv4Address);
                }
            }

            return map;
        }
        catch (LibreNmsApiException)
        {
            return new Dictionary<string, string>();
        }
    }

    private async Task<NeighbourSnapshot> LoadAsync(bool refresh)
    {
        // Not cancelled with any one caller - another may be sharing it.
        var linksTask = _links.GetAsync(refresh);
        var portsTask = _client.Ports.ListAllStatusAsync();
        var arpTask = LoadIpByMacAsync();
        await Task.WhenAll(linksTask, portsTask, arpTask).ConfigureAwait(false);

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

        var snapshot = new NeighbourSnapshot(neighbours, ports, arpTask.Result, DateTimeOffset.Now);
        lock (_gate)
        {
            _latest = snapshot;
        }

        return snapshot;
    }
}
