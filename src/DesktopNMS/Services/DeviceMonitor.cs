using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>Outcome of one device polling cycle.</summary>
public sealed class DevicePollResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<Device> Devices { get; init; }

    /// <summary>
    /// Ids of devices currently in a maintenance window. LibreNMS has no bulk
    /// endpoint for this (see <see cref="IDevicesApi.IsUnderMaintenanceAsync"/>),
    /// so it is scanned separately from the device list itself and throttled
    /// independently - see <see cref="DeviceMonitor"/> - meaning it can lag
    /// the rest of this result by up to that throttle window.
    /// </summary>
    public IReadOnlySet<int> DeviceIdsUnderMaintenance { get; init; } = ImmutableEmptySet;

    public required DateTimeOffset CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    private static readonly IReadOnlySet<int> ImmutableEmptySet = new HashSet<int>();

    public static DevicePollResult Failed(string message) => new()
    {
        Succeeded = false,
        Devices = Array.Empty<Device>(),
        CompletedAt = DateTimeOffset.Now,
        ErrorMessage = message,
    };
}

/// <summary>
/// Polls the full device list (<c>/devices</c>) on a timer, lazily started
/// the same way as <see cref="SensorMonitor"/> - only once something needs
/// it (the Devices tab, or a "device status" dashboard widget) - so a user
/// who never opens either pays nothing extra. Every successful poll also
/// feeds <see cref="IDeviceCache"/> directly, so Alerts/Health/Dashboard's
/// own on-demand device lookups see fresh data for free instead of running
/// their own separate fetch while this is already running. Also runs the
/// per-device maintenance-window scan (see <see cref="DevicePollResult.DeviceIdsUnderMaintenance"/>)
/// that used to live in the Devices tab's own view model, so every consumer
/// of this monitor shares that one scan too instead of each running it.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    /// <summary>
    /// LibreNMS's versioned API has no bulk "which devices are in
    /// maintenance" call, only GET /devices/{id}/maintenance - one device at a
    /// time (see <see cref="IDevicesApi.IsUnderMaintenanceAsync"/>). Checking a
    /// fleet of hundreds of devices therefore means hundreds of requests. This
    /// caps how many run at once so a poll does not look like a burst against
    /// the LibreNMS server.
    /// </summary>
    private const int MaxConcurrentMaintenanceChecks = 16;

    /// <summary>
    /// Maintenance windows do not change second to second, so a poll landing
    /// within this long of the last scan reuses it rather than paying for
    /// hundreds of requests again every single cycle.
    /// </summary>
    private static readonly TimeSpan MinimumMaintenanceRescanInterval = TimeSpan.FromSeconds(60);

    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly AlertMonitor _alertMonitor;
    private readonly ILogger<DeviceMonitor> _logger;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private HashSet<int> _maintenanceIds = new();
    private DateTimeOffset? _lastMaintenanceScan;

    public DeviceMonitor(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        AlertMonitor alertMonitor,
        ILogger<DeviceMonitor> logger)
    {
        _client = client;
        _session = session;
        _settings = settings;
        _devices = devices;
        _alertMonitor = alertMonitor;
        _logger = logger;
    }

    /// <summary>Raised on a background thread when a poll starts, so the UI can show a busy indicator.</summary>
    public event EventHandler? PollStarted;

    /// <summary>Raised on a background thread after every polling cycle.</summary>
    public event EventHandler<DevicePollResult>? Polled;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Seconds until this monitor's next aligned tick (see <see cref="PollAlignment"/>),
    /// for a consumer's own countdown display to start from the true
    /// remaining time rather than a full interval.
    /// </summary>
    public int SecondsUntilNextPoll()
        => PollAlignment.GetSecondsRemaining(_alertMonitor.StartedAt, _settings.Current.PollIntervalSeconds);

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);

        _logger.LogInformation(
            "Device monitor started; polling every {Interval}s",
            _settings.Current.PollIntervalSeconds);
    }

    /// <summary>Asks the loop to poll immediately rather than waiting for the timer.</summary>
    public void RequestRefresh()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Refresh requested while shutting down.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PollOnceAsync(cancellationToken).ConfigureAwait(false);

            // Aligned to AlertMonitor's schedule rather than a plain fixed
            // interval, so this tab's countdown reaches zero at the same
            // moment as every other tab's, regardless of when this monitor
            // itself was started.
            var wait = PollAlignment.GetAlignedWait(_alertMonitor.StartedAt, _settings.Current.PollIntervalSeconds);

            try
            {
                await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!_session.IsConnected)
        {
            return;
        }

        // A manual refresh landing on top of a timer tick must not double-fetch.
        if (!await _pollGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            PollStarted?.Invoke(this, EventArgs.Empty);

            var devices = await _client.Devices.ListAsync(cancellationToken).ConfigureAwait(false);

            _devices.UpdateFrom(devices);

            var maintenanceIds = await RefreshMaintenanceIdsAsync(devices, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Polled {Count} devices", devices.Count);

            Polled?.Invoke(this, new DevicePollResult
            {
                Succeeded = true,
                Devices = devices,
                DeviceIdsUnderMaintenance = maintenanceIds,
                CompletedAt = DateTimeOffset.Now,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Device poll failed");
            Polled?.Invoke(this, DevicePollResult.Failed(ex.ToUserMessage()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device poll failed unexpectedly");
            Polled?.Invoke(this, DevicePollResult.Failed(ex.Message));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Scans every device for an active maintenance window, bounded to
    /// <see cref="MaxConcurrentMaintenanceChecks"/> requests at once, unless
    /// the last scan is still fresh enough to reuse. A single device's check
    /// failing is logged and treated as "not in maintenance" rather than
    /// losing the whole scan.
    /// </summary>
    private async Task<IReadOnlySet<int>> RefreshMaintenanceIdsAsync(IReadOnlyList<Device> devices, CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            _maintenanceIds = new HashSet<int>();
            return _maintenanceIds;
        }

        if (_lastMaintenanceScan is { } last && DateTimeOffset.UtcNow - last < MinimumMaintenanceRescanInterval)
        {
            return _maintenanceIds;
        }

        var found = new ConcurrentBag<int>();
        var failures = 0;

        using var gate = new SemaphoreSlim(MaxConcurrentMaintenanceChecks);

        await Task.WhenAll(devices.Select(async device =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await _client.Devices.IsUnderMaintenanceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false))
                {
                    found.Add(device.DeviceId);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Superseded by a newer poll; leave the previous scan as it was.
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                _logger.LogDebug(ex, "Could not check maintenance status for device {DeviceId}", device.DeviceId);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return _maintenanceIds;
        }

        _maintenanceIds = found.ToHashSet();
        _lastMaintenanceScan = DateTimeOffset.UtcNow;

        if (failures > 0)
        {
            _logger.LogWarning(
                "Maintenance status could not be checked for {Failures} of {Total} device(s)",
                failures,
                devices.Count);
        }

        return _maintenanceIds;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();
        _wake.Dispose();
        _pollGate.Dispose();
    }
}
