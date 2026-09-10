using System;
using System.Collections.Generic;
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

    public required DateTimeOffset CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public static DevicePollResult Failed(string message) => new()
    {
        Succeeded = false,
        Devices = Array.Empty<Device>(),
        CompletedAt = DateTimeOffset.Now,
        ErrorMessage = message,
    };
}

/// <summary>
/// Polls the full device list (<c>/devices</c>) on a timer for the Devices
/// tab, lazily started the same way as <see cref="SensorMonitor"/> - only
/// once the tab has actually been shown, so a user who never opens it pays
/// nothing extra. Every successful poll also feeds <see cref="IDeviceCache"/>
/// directly, so Alerts/Health/Dashboard's own on-demand device lookups see
/// fresh data for free instead of running their own separate fetch while the
/// Devices tab happens to be open.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
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

            _logger.LogDebug("Polled {Count} devices", devices.Count);

            Polled?.Invoke(this, new DevicePollResult
            {
                Succeeded = true,
                Devices = devices,
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
