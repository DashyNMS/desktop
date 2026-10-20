using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>
/// Every port in the fleet with its traffic and error rates
/// (<see cref="IPortsApi.ListAllStatusAsync"/>), fetched once and shared by
/// the Top interfaces, Top errors and Top devices dashboard widgets (#198):
/// three widgets each fetching ~9,000 ports on every refresh would triple the
/// load for the same data. Kept for 30 seconds - long enough to cover every
/// widget asking on the same refresh - with one fetch at a time, however
/// many ask. Fetched only when a widget asks, so with none on the dashboard
/// it costs nothing; widgets ask on each device poll, which already slows
/// down while the app is in the background.
/// </summary>
public interface IFleetPorts
{
    /// <param name="refresh">Fetch again even if the last copy is still fresh (the Dashboard's refresh button).</param>
    Task<IReadOnlyList<Port>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class FleetPorts : IFleetPorts
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(30);

    private readonly ILibreNmsClient _client;
    private readonly object _gate = new();

    private IReadOnlyList<Port>? _latest;
    private DateTimeOffset _loadedAt;
    private Task<IReadOnlyList<Port>>? _inFlight;

    public FleetPorts(ILibreNmsClient client) => _client = client;

    public async Task<IReadOnlyList<Port>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<Port>> task;
        lock (_gate)
        {
            if (!refresh && _latest is { } latest && DateTimeOffset.Now - _loadedAt < FreshFor)
            {
                return latest;
            }

            // Several widgets refreshing together share one fetch.
            if (_inFlight is not { IsCompleted: false })
            {
                _inFlight = LoadAsync();
            }

            task = _inFlight;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Port>> LoadAsync()
    {
        var ports = await _client.Ports.ListAllStatusAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _latest = ports;
            _loadedAt = DateTimeOffset.Now;
        }

        return ports;
    }
}
