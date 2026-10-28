using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Diagnostics;

/// <summary>One logical question, including all internal rounds; accepts no conversation content.</summary>
public sealed class IntelligenceRequestTiming
{
    private readonly object _gate = new();
    private readonly IAppMetrics? _metrics;
    private readonly TimeProvider _time;
    private readonly long? _started;
    private readonly long _generation;
    private long? _first;
    private bool _completed;

    public IntelligenceRequestTiming(IAppMetrics? metrics, TimeProvider? timeProvider = null)
    {
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;
        if (metrics?.IsEnabled == true) { _started = _time.GetTimestamp(); _generation = metrics.ConsentGeneration; }
    }

    public void FirstResponse()
    {
        lock (_gate)
        {
            if (!_completed && _started is not null && _metrics?.IsEnabled == true && _metrics.ConsentGeneration == _generation)
                _first ??= _time.GetTimestamp();
        }
    }

    public void Complete(MetricOutcome outcome)
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            if (_started is not { } start || _metrics is null || !_metrics.IsEnabled || _metrics.ConsentGeneration != _generation) return;
            _metrics.Record(new(MetricName.IntelligenceRequests, 1, Outcome: outcome, Area: MetricArea.Intelligence), _generation);
            _metrics.Record(new(MetricName.IntelligenceDuration, _time.GetElapsedTime(start).TotalMilliseconds,
                Outcome: outcome, Area: MetricArea.Intelligence), _generation);
            if (_first is { } first)
                _metrics.Record(new(MetricName.IntelligenceFirstResponse, _time.GetElapsedTime(start, first).TotalMilliseconds,
                    Outcome: outcome, Area: MetricArea.Intelligence), _generation);
        }
    }

    public static MetricOutcome Failure(Exception error, bool callerCancelled) => error switch
    {
        OperationCanceledException => callerCancelled ? MetricOutcome.Cancelled : MetricOutcome.Timeout,
        TimeoutException => MetricOutcome.Timeout,
        ChatGptSignInException => MetricOutcome.AuthFailure,
        HttpRequestException => MetricOutcome.NetworkFailure,
        IntelligenceException intelligence => intelligence.Failure switch
        {
            IntelligenceFailure.SignIn => MetricOutcome.AuthFailure,
            IntelligenceFailure.PlanUsage => MetricOutcome.PlanLimit,
            IntelligenceFailure.Model => MetricOutcome.ModelFailure,
            IntelligenceFailure.Tools => MetricOutcome.ToolFailure,
            IntelligenceFailure.Network => MetricOutcome.NetworkFailure,
            _ => MetricOutcome.Failed,
        },
        _ => MetricOutcome.Failed,
    };
}
