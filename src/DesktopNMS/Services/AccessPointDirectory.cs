using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;

namespace DesktopNMS.Services;

/// <summary>Every access point LibreNMS's neighbour links show, with its switch port's current state - see <see cref="AccessPoints"/>.</summary>
public sealed record AccessPointSnapshot(
    IReadOnlyList<AccessPoint> AccessPoints,
    IReadOnlyDictionary<int, Port> Ports,
    DateTimeOffset LoadedAt)
{
    public static AccessPointSnapshot Empty { get; } = new(Array.Empty<AccessPoint>(), new Dictionary<int, Port>(), DateTimeOffset.MinValue);

    public Port? PortOf(AccessPoint accessPoint) => Ports.GetValueOrDefault(accessPoint.SwitchPortId);
}

/// <summary>
/// The fleet's access points (#55), shared by the Access points page and
/// each controller's Wireless section. Building the list takes two
/// fleet-wide calls (every link, every port - a couple of seconds), so a
/// result is kept for a short while and callers asking at the same time
/// share one fetch.
/// </summary>
public interface IAccessPointDirectory
{
    /// <param name="refresh">Fetch afresh even if a recent result is to hand - a user's refresh.</param>
    Task<AccessPointSnapshot> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class AccessPointDirectory : IAccessPointDirectory
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    private readonly ILibreNmsClient _client;
    private readonly IDeviceCache _devices;
    private readonly object _gate = new();

    private AccessPointSnapshot? _latest;
    private Task<AccessPointSnapshot>? _inFlight;

    public AccessPointDirectory(ILibreNmsClient client, IDeviceCache devices)
    {
        _client = client;
        _devices = devices;
    }

    public Task<AccessPointSnapshot> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
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

    private async Task<AccessPointSnapshot> LoadAsync()
    {
        // Not cancelled with any one caller - another may be sharing it.
        var linksTask = _client.Links.ListAllAsync();
        var portsTask = _client.Ports.ListAllStatusAsync();
        await Task.WhenAll(linksTask, portsTask).ConfigureAwait(false);

        var accessPoints = AccessPoints.FromLinks(linksTask.Result);
        var ports = portsTask.Result
            .GroupBy(p => p.PortId)
            .ToDictionary(g => g.Key, g => g.First());

        // The switches' names come from the device cache - make sure it has them.
        await _devices.EnsureCurrentAsync(accessPoints.Select(ap => ap.SwitchDeviceId).Distinct()).ConfigureAwait(false);

        var snapshot = new AccessPointSnapshot(accessPoints, ports, DateTimeOffset.Now);
        lock (_gate)
        {
            _latest = snapshot;
        }

        return snapshot;
    }
}
