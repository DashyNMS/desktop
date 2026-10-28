using System.Diagnostics;
using DesktopNMS.Core.Services;

namespace DesktopNMS.Diagnostics;

public sealed record MemorySnapshot(long WorkingSetBytes, long ManagedHeapBytes);

/// <summary>Reads only current-process totals at a fixed low frequency, never memory contents.</summary>
public sealed class MemoryMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly IAppMetrics _metrics;
    private readonly IAppActivity _activity;
    private readonly TimeProvider _time;
    private readonly Func<MemorySnapshot> _read;
    private readonly ITimer _timer;
    private bool _enabled, _disposed, _reading;
    private long _lastRead;

    public MemoryMonitor(IAppMetrics metrics, IAppActivity activity, TimeProvider? timeProvider = null,
        Func<MemorySnapshot>? read = null)
    {
        _metrics = metrics; _activity = activity;
        _time = timeProvider ?? TimeProvider.System;
        _read = read ?? ReadCurrent;
        _timer = _time.CreateTimer(_ => Sample(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static MemorySnapshot ReadCurrent()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false));
    }

    public void Configure()
    {
        lock (_gate)
        {
            if (_disposed || _enabled == _metrics.IsEnabled) return;
            _enabled = _metrics.IsEnabled;
            _lastRead = _time.GetTimestamp();
            _timer.Change(_enabled ? Interval : Timeout.InfiniteTimeSpan, _enabled ? Interval : Timeout.InfiniteTimeSpan);
        }
    }

    private void Sample()
    {
        long generation;
        MetricMode mode;
        lock (_gate)
        {
            if (_disposed || !_enabled || _reading || !_metrics.IsEnabled || _time.GetElapsedTime(_lastRead) < Interval) return;
            _reading = true;
            _lastRead = _time.GetTimestamp(); // Prevent catch-up bursts and rapid retries after a failed read.
            generation = _metrics.ConsentGeneration;
            mode = _activity.IsInBackground ? MetricMode.Background : MetricMode.Interactive;
        }
        try
        {
            var snapshot = _read();
            lock (_gate)
            {
                if (_disposed || !_enabled || _metrics.ConsentGeneration != generation) return;
                if (snapshot.WorkingSetBytes is >= 0 and <= 1_099_511_627_776)
                    _metrics.Record(new(MetricName.WorkingSet, snapshot.WorkingSetBytes, mode), generation);
                if (snapshot.ManagedHeapBytes is >= 0 and <= 1_099_511_627_776)
                    _metrics.Record(new(MetricName.ManagedHeap, snapshot.ManagedHeapBytes, mode), generation);
            }
        }
        catch (Exception) { /* Unsupported/failed process statistics are skipped, never reported as app errors. */ }
        finally { lock (_gate) _reading = false; }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _enabled = false; _timer.Dispose(); }
    }
}
