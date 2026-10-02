using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>
/// Every device's LLDP/CDP links (resources/links), fetched once and shared
/// (#60): the network map, the Neighbours views and each Device Details
/// window all need the whole list, and each used to fetch it for itself.
/// Kept for two minutes; one fetch at a time, however many ask.
/// </summary>
public interface IFleetLinks
{
    /// <param name="refresh">Fetch again even if the last copy is still fresh (a Refresh click).</param>
    Task<IReadOnlyList<NetworkLink>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class FleetLinks : IFleetLinks
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    private readonly ILibreNmsClient _client;
    private readonly object _gate = new();

    private IReadOnlyList<NetworkLink>? _latest;
    private DateTimeOffset _loadedAt;
    private Task<IReadOnlyList<NetworkLink>>? _inFlight;

    public FleetLinks(ILibreNmsClient client) => _client = client;

    public async Task<IReadOnlyList<NetworkLink>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<NetworkLink>> task;
        lock (_gate)
        {
            if (!refresh && _latest is { } latest && DateTimeOffset.Now - _loadedAt < FreshFor)
            {
                return latest;
            }

            if (_inFlight is not { IsCompleted: false })
            {
                _inFlight = LoadAsync();
            }

            task = _inFlight;
        }

        // A caller giving up (its window closed) doesn't cancel the shared fetch.
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<NetworkLink>> LoadAsync()
    {
        var links = await _client.Links.ListAllAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _latest = links;
            _loadedAt = DateTimeOffset.Now;
        }

        return links;
    }
}
