using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Keeps a device_id to <see cref="Device"/> map.
/// </summary>
/// <remarks>
/// The alerts endpoint returns only <c>hostname</c>, so showing sysName or the
/// LibreNMS display name means having the device list to hand. It is refreshed
/// on a slow timer, and immediately whenever an alert names a device that is not
/// in the map yet.
/// </remarks>
public interface IDeviceCache
{
    /// <summary>The device, or null if the list has not loaded or does not contain it.</summary>
    Device? Get(int deviceId);

    /// <summary>True once a device list has been fetched at least once.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Refreshes if the map is stale or is missing any of the given device ids.
    /// Never throws: a failed device fetch must not fail the alert poll.
    /// </summary>
    Task EnsureCurrentAsync(IEnumerable<int> requiredDeviceIds, CancellationToken cancellationToken = default);

    /// <summary>Forces a refresh on the next <see cref="EnsureCurrentAsync"/> call.</summary>
    void Invalidate();
}

public sealed class DeviceCache : IDeviceCache
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    // Stops a burst of unknown device ids turning into a burst of /devices calls.
    private static readonly TimeSpan MinimumGapAfterMiss = TimeSpan.FromSeconds(30);

    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ILogger<DeviceCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<int, Device> _devices = new();
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public DeviceCache(ILibreNmsClient client, ISessionService session, ILogger<DeviceCache> logger)
    {
        _client = client;
        _session = session;
        _logger = logger;
    }

    public bool IsLoaded { get; private set; }

    public Device? Get(int deviceId)
    {
        // Reference read of an immutable dictionary; replaced wholesale on refresh.
        var snapshot = _devices;
        return snapshot.TryGetValue(deviceId, out var device) ? device : null;
    }

    public void Invalidate() => _lastRefresh = DateTimeOffset.MinValue;

    public async Task EnsureCurrentAsync(IEnumerable<int> requiredDeviceIds, CancellationToken cancellationToken = default)
    {
        if (!_session.IsConnected)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var snapshot = _devices;

        var stale = now - _lastRefresh >= RefreshInterval;
        var missing = requiredDeviceIds.Any(id => !snapshot.ContainsKey(id));

        if (!stale && !missing)
        {
            return;
        }

        // A device that is genuinely absent from LibreNMS would otherwise make
        // every poll refetch the whole device list.
        if (!stale && now - _lastAttempt < MinimumGapAfterMiss)
        {
            return;
        }

        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _lastAttempt = DateTimeOffset.UtcNow;

            var devices = await _client.Devices.ListAsync(cancellationToken).ConfigureAwait(false);

            var map = new Dictionary<int, Device>(devices.Count);
            foreach (var device in devices)
            {
                map[device.DeviceId] = device;
            }

            _devices = map;
            _lastRefresh = DateTimeOffset.UtcNow;
            IsLoaded = true;

            _logger.LogDebug("Device cache refreshed: {Count} devices", map.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LibreNmsApiException ex)
        {
            // Falling back to the hostname from the alert is a perfectly usable
            // outcome, so this is a warning rather than a failed poll.
            _logger.LogWarning(ex, "Could not refresh the device list; alert hostnames will be used");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the device list");
        }
        finally
        {
            _gate.Release();
        }
    }
}
