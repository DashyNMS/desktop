namespace DesktopNMS.Diagnostics;

// Only built-in dimensions can enter the queue. No caller-supplied names or tags.
public enum MetricName { StartupDuration, ScreenLoadDuration, ApiDuration, FeatureUse, UpdateOutcome, IntelligenceDuration, IntelligenceFirstResponse, UiStallCount, UiStallDuration, WorkingSet, ManagedHeap, ApiRequestCount, IntelligenceRequests }
public enum MetricMode { Interactive, Background }
public enum MetricOutcome { Success, Failed, Cancelled, Timeout, NetworkFailure, ClientError, ServerError, Cached, AuthFailure, PlanLimit, ModelFailure, ToolFailure }
public enum MetricArea { Application, Dashboard, DeviceDetails, Alerts, Health, Devices, Sensors, Maps, Intelligence, DashboardEditor, Widget, UpdateCheck, UpdateDownload, UpdateInstall, UpdateStartup, DeviceList, ApiOther, WidgetSensors, WidgetAlerts, WidgetAlertsGauge, WidgetDeviceStatus, WidgetRecentlyViewed, WidgetPinnedDevices, WidgetGraph, WidgetWireless, WidgetTopInterfaces, WidgetTopErrors, WidgetTopDevices, WidgetEventLog, WidgetGraylog }
public sealed record MetricObservation(MetricName Name, double Value, MetricMode Mode = MetricMode.Interactive,
    MetricOutcome Outcome = MetricOutcome.Success, MetricArea Area = MetricArea.Application);
public sealed record MetricPoint(DateTimeOffset Timestamp, MetricObservation Observation);

public interface IAppMetrics
{
    bool IsEnabled => true;
    long ConsentGeneration => 0;
    void Record(MetricObservation observation);
    void Record(MetricObservation observation, long expectedGeneration)
    {
        if (IsEnabled && ConsentGeneration == expectedGeneration) Record(observation);
    }
}

/// <summary>Bounds work and payloads before they reach the persisted diagnostics queue.</summary>
public sealed class AppMetrics : IAppMetrics, IDisposable
{
    public const int MaximumPoints = 32;
    private readonly object _gate = new();
    private readonly CrashReporter _reporter;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly List<MetricPoint> _points = [];
    private readonly Dictionary<(MetricName, MetricMode, MetricOutcome, MetricArea), int> _samples = [];
    private bool _enabled;
    private bool _disposed;
    private long _generation;
    public bool IsEnabled { get { lock (_gate) return _enabled && !_disposed; } }
    public long ConsentGeneration { get { lock (_gate) return _generation; } }

    public AppMetrics(CrashReporter reporter, TimeProvider? timeProvider = null)
    {
        _reporter = reporter;
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Flush(send: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            if (!enabled) { _points.Clear(); _samples.Clear(); }
            _timer.Change(enabled ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan,
                enabled ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan);
        }
    }

    void IAppMetrics.Record(MetricObservation observation, long expectedGeneration)
        => Record(observation, (long?)expectedGeneration);

    public void Record(MetricObservation observation) => Record(observation, expectedGeneration: null);

    public void Record(MetricObservation observation, long? expectedGeneration)
    {
        lock (_gate)
        {
            if (!_enabled || _disposed || (expectedGeneration is { } generation && generation != _generation) || !IsValid(observation)) return;
            var key = (observation.Name, observation.Mode, observation.Outcome, observation.Area);
            var counter = IsCounter(observation.Name);
            if (counter)
            {
                var index = _points.FindIndex(p => (p.Observation.Name, p.Observation.Mode, p.Observation.Outcome, p.Observation.Area) == key);
                if (index >= 0)
                {
                    var point = _points[index];
                    _points[index] = point with { Observation = observation with { Value = Math.Min(1_000_000, point.Observation.Value + observation.Value) } };
                    return;
                }
            }
            else
            {
                if (!_samples.TryGetValue(key, out var sample) && _samples.Count >= MaximumPoints) return;
                _samples[key] = (sample + 1) % 10;
                if (sample != 0) return; // First, then every tenth observation per built-in series.
            }
            if (_points.Count < MaximumPoints) _points.Add(new(_time.GetUtcNow(), observation));
        }
    }

    public void Flush(bool send = false)
    {
        // Read the queue generation outside the collection lock: disk IO must not
        // hold up Record() on the UI thread. A concurrent consent change rejects
        // the snapshot, including withdrawal followed by immediate reconsent.
        var reporterGeneration = _reporter.ConsentGeneration;
        MetricPoint[] snapshot;
        lock (_gate)
        {
            if (!_enabled || _disposed) return;
            _points.RemoveAll(point => point.Timestamp < _time.GetUtcNow().AddDays(-7));
            snapshot = _points.ToArray();
            _points.Clear();
        }
        if (snapshot.Length > 0) _reporter.RecordMetrics(snapshot, reporterGeneration);
        if (send) _ = _reporter.SendPendingAsync();
    }

    internal static bool IsCounter(MetricName name) => name is MetricName.FeatureUse or MetricName.UpdateOutcome or MetricName.UiStallCount or MetricName.ApiRequestCount or MetricName.IntelligenceRequests;
    internal static bool IsGauge(MetricName name) => name is MetricName.WorkingSet or MetricName.ManagedHeap;
    internal static bool IsValid(MetricObservation? observation) => observation is not null &&
        Enum.IsDefined(observation.Name) && Enum.IsDefined(observation.Mode) && Enum.IsDefined(observation.Outcome) && Enum.IsDefined(observation.Area) &&
        double.IsFinite(observation.Value) && observation.Value >= 0 &&
        (IsCounter(observation.Name) ? observation.Value is > 0 and <= 1_000_000 && observation.Value == Math.Truncate(observation.Value) :
         observation.Value <= (IsGauge(observation.Name) ? 1_099_511_627_776 : 86_400_000));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Flush();
            _disposed = true;
            _points.Clear();
            _samples.Clear();
            _timer.Dispose();
        }
    }
}
