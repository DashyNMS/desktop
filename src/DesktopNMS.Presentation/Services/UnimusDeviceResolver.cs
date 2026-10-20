using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Matches a LibreNMS device to its Unimus entry (issue #115) by fetching
/// Unimus's whole device list once and matching client-side against
/// <see cref="UnimusAddressCandidates"/> - see <see cref="UnimusDeviceMatcher"/>'s
/// own remarks for why this, rather than Unimus's <c>findByAddress</c>
/// endpoint, is how this has to work.
/// </summary>
public interface IUnimusDeviceResolver
{
    /// <summary>The matched Unimus device id, or null if Unimus isn't configured or no candidate matched.</summary>
    Task<int?> ResolveAsync(Device device, CancellationToken cancellationToken = default);

    /// <summary>Drops the cached device list, e.g. after Unimus settings change or "Backup now" adds a device Unimus didn't know about before.</summary>
    void Clear();
}

public sealed class UnimusDeviceResolver : IUnimusDeviceResolver
{
    // Shorter than a per-device cache would need to be, since one refresh
    // now serves every device rather than being paid for individually - a
    // newly-added Unimus device becomes matchable within this window instead
    // of needing an hour (LibreNMS's own per-device cache duration) or an
    // app restart.
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(15);

    private readonly IUnimusApi _unimus;
    private readonly ISettingsStore _settings;
    private readonly ILogger<UnimusDeviceResolver> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private IReadOnlyList<UnimusDevice>? _devices;
    private DateTime _cachedAtUtc;

    public UnimusDeviceResolver(IUnimusApi unimus, ISettingsStore settings, ILogger<UnimusDeviceResolver> logger)
    {
        _unimus = unimus;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int?> ResolveAsync(Device device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!_unimus.IsConfigured)
        {
            return null;
        }

        var devices = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        if (devices is null)
        {
            return null;
        }

        var candidates = UnimusAddressCandidates.ForDevice(device, _settings.Current.Unimus.MyDomain);
        return UnimusDeviceMatcher.Match(devices, candidates)?.Id;
    }

    public void Clear()
    {
        lock (_loadGate)
        {
            _devices = null;
        }
    }

    private async Task<IReadOnlyList<UnimusDevice>?> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_devices is not null && DateTime.UtcNow - _cachedAtUtc < ListCacheTtl)
        {
            return _devices;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have just refreshed it while this one waited.
            if (_devices is not null && DateTime.UtcNow - _cachedAtUtc < ListCacheTtl)
            {
                return _devices;
            }

            _devices = await _unimus.ListAllDevicesAsync(cancellationToken).ConfigureAwait(false);
            _cachedAtUtc = DateTime.UtcNow;
            return _devices;
        }
        catch (UnimusApiException ex)
        {
            _logger.LogWarning(ex, "Could not fetch Unimus's device list");
            return _devices; // stale is better than nothing if we have one
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
