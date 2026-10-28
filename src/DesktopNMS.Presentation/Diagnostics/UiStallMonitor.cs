using System.Diagnostics;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Diagnostics;

/// <summary>A bounded dispatcher-latency probe; does not inspect frames, input or UI content.</summary>
public sealed class UiStallMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumWorkerGap = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly AppMetrics _metrics;
    private readonly IAppActivity _activity;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Func<bool> _debugging;
    private readonly Func<bool> _foregroundOnUi;
    private readonly ITimer _timer;
    private bool _enabled, _disposed, _pending, _valid;
    private long _lastTick, _sent, _generation;
    private DateTimeOffset _lastWall;

    public UiStallMonitor(AppMetrics metrics, IAppActivity activity, IUiDispatcher dispatcher,
        TimeProvider? timeProvider = null, Func<bool>? debugging = null, Func<bool>? foregroundOnUi = null)
    {
        _metrics = metrics;
        _activity = activity;
        _dispatcher = dispatcher;
        _time = timeProvider ?? TimeProvider.System;
        _debugging = debugging ?? (() => Debugger.IsAttached);
        _foregroundOnUi = foregroundOnUi ?? (() => !_activity.IsInBackground);
        _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _activity.Changed += OnActivityChanged;
    }

    public void Configure()
    {
        lock (_gate)
        {
            if (_disposed || _enabled == _metrics.IsEnabled) return;
            _enabled = _metrics.IsEnabled;
            _valid = false;
            ResetClock();
            _timer.Change(_enabled ? Interval : Timeout.InfiniteTimeSpan, _enabled ? Interval : Timeout.InfiniteTimeSpan);
        }
    }

    private void ResetClock() { _lastTick = _time.GetTimestamp(); _lastWall = _time.GetUtcNow(); }
    private bool WorkerGap() => _time.GetElapsedTime(_lastTick) > MaximumWorkerGap ||
        _time.GetUtcNow() - _lastWall > MaximumWorkerGap ||
        Math.Abs(((_time.GetUtcNow() - _lastWall) - _time.GetElapsedTime(_lastTick)).TotalSeconds) > 1;

    private void OnActivityChanged(object? sender, EventArgs args)
    {
        lock (_gate) { _valid = false; ResetClock(); }
    }

    private void Tick()
    {
        lock (_gate)
        {
            var excluded = WorkerGap() || !_enabled || _disposed || !_metrics.IsEnabled || _activity.IsInBackground || _debugging();
            ResetClock();
            if (excluded) { _valid = false; return; }
            if (_pending) return;
            _pending = _valid = true;
            _sent = _time.GetTimestamp();
            _generation = _metrics.ConsentGeneration;
        }
        try { _dispatcher.Post(Acknowledge, UiPriority.Normal); }
        catch (Exception) { lock (_gate) { _pending = _valid = false; } }
    }

    private void Acknowledge()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            if (!_valid || !_enabled || _disposed || WorkerGap() || _debugging() || !_foregroundOnUi()) return;
            var duration = _time.GetElapsedTime(_sent);
            _valid = false;
            if (duration < StallThreshold) return;
            _metrics.Record(new(MetricName.UiStallCount, 1), _generation);
            _metrics.Record(new(MetricName.UiStallDuration, duration.TotalMilliseconds), _generation);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = _valid = false;
            _timer.Dispose();
            _activity.Changed -= OnActivityChanged;
        }
    }
}
