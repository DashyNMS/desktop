using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>Outcome of one sensor polling cycle.</summary>
public sealed class SensorPollResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<Sensor> Sensors { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public static SensorPollResult Failed(string message) => new()
    {
        Succeeded = false,
        Sensors = Array.Empty<Sensor>(),
        CompletedAt = DateTimeOffset.Now,
        ErrorMessage = message,
    };
}

/// <summary>
/// Polls every sensor across the fleet (<c>/resources/sensors</c>) on a
/// timer, shared by the Health tab and the Dashboard's Sensors widgets so
/// the two do not each run their own independent poll of the same data.
/// Mirrors <see cref="AlertMonitor"/>: lazily started by whichever consumer
/// shows first (see <see cref="Start"/>, safe to call repeatedly), running in
/// the background rather than on a UI timer so a slow server cannot freeze
/// the window.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IDeviceCache _devices;
    private readonly AlertMonitor _alertMonitor;
    private readonly ILogger<SensorMonitor> _logger;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public SensorMonitor(
        ILibreNmsClient client,
        ISessionService session,
        ISettingsStore settings,
        IDeviceCache devices,
        AlertMonitor alertMonitor,
        ILogger<SensorMonitor> logger)
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
    public event EventHandler<SensorPollResult>? Polled;

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
            "Sensor monitor started; polling every {Interval}s",
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

            var sensors = await _client.Sensors.ListAsync(cancellationToken).ConfigureAwait(false);

            // Device names for consumers' lists come from here, not from the
            // sensor rows, so refresh before publishing the result. This never
            // throws, and pre-warming it here means every consumer's own
            // handler can stay a plain synchronous update.
            await _devices
                .EnsureCurrentAsync(GetDistinctDeviceIds(sensors), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Polled {Count} sensors", sensors.Count);

            Polled?.Invoke(this, new SensorPollResult
            {
                Succeeded = true,
                Sensors = sensors,
                CompletedAt = DateTimeOffset.Now,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Sensor poll failed");
            Polled?.Invoke(this, SensorPollResult.Failed(ex.ToUserMessage()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sensor poll failed unexpectedly");
            Polled?.Invoke(this, SensorPollResult.Failed(ex.Message));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private static IEnumerable<int> GetDistinctDeviceIds(IReadOnlyList<Sensor> sensors)
        => sensors.Select(s => s.DeviceId).Distinct();

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
