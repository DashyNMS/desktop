namespace DesktopNMS.Diagnostics;

/// <summary>One managed-entry-to-ready measurement, excluding explicit session restoration.</summary>
public sealed class StartupTiming
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Func<bool> _sample;
    private long? _paused;
    private TimeSpan _excluded;
    private bool _completed;

    public StartupTiming(TimeProvider? timeProvider = null, Func<bool>? sample = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _sample = sample ?? (() => Random.Shared.Next(10) == 0);
        _started = _time.GetTimestamp();
    }

    public void PauseForSessionRestore()
    {
        if (!_completed && _paused is null) _paused = _time.GetTimestamp();
    }

    public void ResumeAfterSessionRestore()
    {
        if (_paused is not { } start) return;
        _excluded += _time.GetElapsedTime(start);
        _paused = null;
    }

    public void Ready(IAppMetrics metrics, bool consentedAtStartup, MetricMode mode)
    {
        if (_completed || _paused is not null) return;
        _completed = true;
        if (!consentedAtStartup || !_sample()) return;
        var elapsed = _time.GetElapsedTime(_started) - _excluded;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(5)) return;
        metrics.Record(new(MetricName.StartupDuration, elapsed.TotalMilliseconds, mode));
    }
}
