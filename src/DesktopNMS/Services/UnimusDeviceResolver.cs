using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// Matches a LibreNMS device to its Unimus entry (issue #115), trying
/// <see cref="UnimusAddressCandidates"/> in order via
/// <see cref="IUnimusApi.FindDeviceAsync"/> - the same one-address-at-a-time
/// approach LibreNMS's own server-side Unimus client uses (there is no
/// documented bulk "list every device" endpoint to match against client-side
/// instead), so a per-device result is cached for an hour, matching that
/// client's own cache duration, rather than re-resolving on every Config tab
/// open.
/// </summary>
public interface IUnimusDeviceResolver
{
    /// <summary>The matched Unimus device id, or null if Unimus isn't configured or no candidate matched.</summary>
    Task<int?> ResolveAsync(Device device, CancellationToken cancellationToken = default);

    /// <summary>Drops the cache for one device - e.g. after "Backup now" changes what Unimus knows about it.</summary>
    void Invalidate(int deviceId);

    /// <summary>Drops the whole cache, e.g. after Unimus settings change.</summary>
    void Clear();
}

public sealed class UnimusDeviceResolver : IUnimusDeviceResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IUnimusApi _unimus;
    private readonly ISettingsStore _settings;
    private readonly ILogger<UnimusDeviceResolver> _logger;
    private readonly ConcurrentDictionary<int, (int? UnimusDeviceId, DateTime CachedAtUtc)> _cache = new();

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

        if (_cache.TryGetValue(device.DeviceId, out var cached) && DateTime.UtcNow - cached.CachedAtUtc < CacheTtl)
        {
            return cached.UnimusDeviceId;
        }

        var candidates = UnimusAddressCandidates.ForDevice(device, _settings.Current.Unimus.MyDomain);

        foreach (var candidate in candidates)
        {
            UnimusDevice? match;
            try
            {
                match = await _unimus.FindDeviceAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (UnimusApiException ex)
            {
                // An auth/server error is not "no match" - do not cache it,
                // so the next attempt (e.g. after fixing the token) retries
                // rather than being stuck on a false negative for an hour.
                _logger.LogWarning(ex, "Could not query Unimus for {Candidate} (device {DeviceId})", candidate, device.DeviceId);
                return null;
            }

            if (match is not null)
            {
                _cache[device.DeviceId] = (match.Id, DateTime.UtcNow);
                return match.Id;
            }
        }

        _cache[device.DeviceId] = (null, DateTime.UtcNow);
        return null;
    }

    public void Invalidate(int deviceId) => _cache.TryRemove(deviceId, out _);

    public void Clear() => _cache.Clear();
}
