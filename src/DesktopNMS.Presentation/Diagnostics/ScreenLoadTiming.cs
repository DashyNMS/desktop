namespace DesktopNMS.Diagnostics;

/// <summary>Tracks a visible visit until its primary data is applied, never background polls.</summary>
public sealed class ScreenLoadTiming(IAppMetrics? metrics, MetricArea area, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _started;
    private long _generation;

    public void Begin(bool cached)
    {
        if (_started is not null || metrics is null || !metrics.IsEnabled) return;
        _started = _time.GetTimestamp();
        _generation = metrics.ConsentGeneration;
        if (cached) Complete(MetricOutcome.Cached);
    }

    public void Complete(MetricOutcome outcome)
    {
        if (_started is not { } started) return;
        _started = null;
        if (metrics is null || !metrics.IsEnabled || metrics.ConsentGeneration != _generation) return;
        var duration = _time.GetElapsedTime(started);
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) return;
        metrics.Record(new(MetricName.ScreenLoadDuration, duration.TotalMilliseconds, Outcome: outcome, Area: area));
    }
}
