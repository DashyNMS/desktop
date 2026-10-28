using DesktopNMS.Core.Configuration;
using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class AppMetricsTests
{
    [Theory]
    [InlineData(0, true, false, true)]
    [InlineData(1, true, true, false)]
    [InlineData(99, true, false, true)]
    [InlineData(-1, true, false, true)]
    public void Broader_collection_requires_the_exact_disclosure_version(int version, bool launches, bool metrics, bool prompt)
    {
        var settings = new AppSettings { DiagnosticsSharing = DiagnosticsSharingPreference.ErrorsAndUsage, DiagnosticsConsentVersion = version };
        Assert.True(DiagnosticsConsent.Errors(settings));
        Assert.Equal(launches, DiagnosticsConsent.Launches(settings));
        Assert.Equal(metrics, DiagnosticsConsent.Metrics(settings));
        Assert.Equal(prompt, DiagnosticsConsent.NeedsChoice(settings));
    }

    [Theory]
    [InlineData(DiagnosticsSharingPreference.None)]
    [InlineData(DiagnosticsSharingPreference.ErrorsOnly)]
    public void Existing_declines_are_not_reprompted(DiagnosticsSharingPreference preference)
    {
        var settings = new AppSettings { DiagnosticsSharing = preference };
        Assert.False(DiagnosticsConsent.Metrics(settings));
        Assert.False(DiagnosticsConsent.NeedsChoice(settings));
    }

    [Fact]
    public async Task Counters_aggregate_and_timings_are_sampled_before_offline_persistence()
    {
        using var folder = new Folder();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink);
        using var metrics = new AppMetrics(reporter);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        metrics.Record(new(MetricName.FeatureUse, 1)); // Before consent: discarded.
        metrics.Configure(true);
        for (var i = 0; i < 20; i++)
        {
            metrics.Record(new(MetricName.FeatureUse, 1, Area: MetricArea.Maps));
            metrics.Record(new(MetricName.StartupDuration, i));
        }
        metrics.Flush();
        using var restarted = new CrashReporter(Context, sink);
        restarted.ConfigureMetrics(folder.Path, false, true, true);
        await restarted.SendPendingAsync();
        var batch = Assert.IsType<MetricsReport>(Assert.Single(sink.Reports));
        Assert.Equal(3, batch.Points.Count);
        Assert.Equal(20, Assert.Single(batch.Points, p => p.Observation.Name == MetricName.FeatureUse).Observation.Value);
        Assert.Equal(new[] { 0d, 10d }, batch.Points.Where(p => p.Observation.Name == MetricName.StartupDuration).Select(p => p.Observation.Value));
    }

    [Fact]
    public async Task Withdrawal_clears_buffer_and_only_the_affected_queue()
    {
        using var folder = new Folder();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink);
        using var metrics = new AppMetrics(reporter);
        reporter.ConfigureMetrics(folder.Path, true, true, true);
        metrics.Configure(true);
        metrics.Record(new(MetricName.FeatureUse, 1));
        metrics.Flush();
        reporter.Capture(new Exception(), CrashOrigin.Application);
        metrics.Record(new(MetricName.FeatureUse, 1));
        metrics.Configure(false);
        reporter.ConfigureMetrics(folder.Path, true, true, false); // Launch-only consent.
        metrics.Flush();
        await reporter.SendPendingAsync();
        Assert.IsType<CrashReport>(Assert.Single(sink.Reports));
        metrics.Configure(true);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        metrics.Flush();
        await reporter.SendPendingAsync();
        Assert.Single(sink.Reports);
    }

    [Fact]
    public async Task Collection_and_queue_are_bounded_and_invalid_values_never_escape()
    {
        using var folder = new Folder();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink);
        using var metrics = new AppMetrics(reporter);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        metrics.Configure(true);
        metrics.Record(new((MetricName)999, 1));
        metrics.Record(new(MetricName.ApiDuration, double.NaN));
        metrics.Record(new(MetricName.FeatureUse, -1));
        metrics.Record(new(MetricName.FeatureUse, 0.5));
        metrics.Record(new(MetricName.ApiDuration, double.PositiveInfinity));
        for (var i = 0; i < 10000; i++) metrics.Record(new(MetricName.ApiDuration, i));
        metrics.Flush();
        await reporter.SendPendingAsync();
        var batch = Assert.IsType<MetricsReport>(Assert.Single(sink.Reports));
        Assert.Equal(AppMetrics.MaximumPoints, batch.Points.Count);
        Assert.All(batch.Points, p => Assert.Equal(MetricName.ApiDuration, p.Observation.Name));
        for (var i = 0; i < 20; i++)
        {
            metrics.Record(new(MetricName.FeatureUse, 1));
            metrics.Flush();
        }
        Assert.Equal(10, Directory.GetFiles(System.IO.Path.Combine(folder.Path, "crash-reports"), "*.json").Length);
        Assert.All(Directory.GetFiles(System.IO.Path.Combine(folder.Path, "crash-reports"), "*.json"), path => Assert.True(new FileInfo(path).Length <= 32768));
    }

    [Fact]
    public async Task Metrics_backoff_and_minute_pacing_do_not_hold_up_managed_errors()
    {
        using var folder = new Folder();
        var clock = new Clock();
        var sink = new Transport { Send = report => report is MetricsReport ? new(CrashSendDisposition.Retry, TimeSpan.FromMinutes(3)) : new(CrashSendDisposition.Accepted) };
        using var reporter = new CrashReporter(Context, sink, clock);
        reporter.ConfigureMetrics(folder.Path, true, true, true);
        reporter.RecordMetrics([new(clock.GetUtcNow(), new(MetricName.FeatureUse, 1))]);
        reporter.Capture(new Exception(), CrashOrigin.Application);
        await reporter.SendPendingAsync();
        Assert.Equal(2, sink.Reports.Count);
        Assert.Contains(sink.Reports, p => p is CrashReport);
        await reporter.SendPendingAsync();
        Assert.Equal(2, sink.Reports.Count);
        clock.Advance(TimeSpan.FromMinutes(3));
        sink.Send = _ => new(CrashSendDisposition.Accepted);
        await reporter.SendPendingAsync();
        reporter.RecordMetrics([new(clock.GetUtcNow(), new(MetricName.FeatureUse, 1))]);
        await reporter.SendPendingAsync();
        Assert.Equal(3, sink.Reports.Count);
        clock.Advance(TimeSpan.FromMinutes(1));
        await reporter.SendPendingAsync();
        Assert.Equal(4, sink.Reports.Count);
    }

    [Fact]
    public async Task A_global_rate_limit_holds_both_categories()
    {
        using var folder = new Folder();
        var clock = new Clock();
        var sink = new Transport { Send = _ => new(CrashSendDisposition.Retry, TimeSpan.FromMinutes(3), RetryAllCategories: true) };
        using var reporter = new CrashReporter(Context, sink, clock);
        reporter.ConfigureMetrics(folder.Path, true, true, true);
        reporter.RecordMetrics([new(clock.GetUtcNow(), new(MetricName.FeatureUse, 1))]);
        reporter.Capture(new Exception(), CrashOrigin.Application);
        await reporter.SendPendingAsync();
        Assert.Single(sink.Reports);
        clock.Advance(TimeSpan.FromMinutes(3));
        sink.Send = _ => new(CrashSendDisposition.Accepted);
        await reporter.SendPendingAsync();
        Assert.Equal(3, sink.Reports.Count);
    }

    [Fact]
    public async Task Minute_timer_persists_and_sends_then_stops_on_withdrawal()
    {
        using var folder = new Folder();
        var clock = new Clock();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink, clock);
        using var metrics = new AppMetrics(reporter, clock);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.Timer!.Period);
        metrics.Configure(true);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Timer.Period);
        metrics.Record(new(MetricName.FeatureUse, 2));
        clock.Advance(TimeSpan.FromMinutes(1));
        clock.Timer.Tick();
        await reporter.SendPendingAsync();
        Assert.IsType<MetricsReport>(Assert.Single(sink.Reports));
        metrics.Configure(false);
        Assert.Equal(Timeout.InfiniteTimeSpan, clock.Timer.Period);
        metrics.Record(new(MetricName.FeatureUse, 100));
        clock.Timer.Tick();
        await reporter.SendPendingAsync();
        Assert.Single(sink.Reports);
    }

    [Fact]
    public void Reapplying_the_bound_usage_value_cannot_expand_legacy_consent()
    {
        var settings = new AppSettings { DiagnosticsSharing = DiagnosticsSharingPreference.ErrorsAndUsage };
        Assert.False(DiagnosticsConsent.ChangeUsageChoice(settings, true));
        Assert.Equal(0, settings.DiagnosticsConsentVersion);
        Assert.False(DiagnosticsConsent.Metrics(settings));
        Assert.True(DiagnosticsConsent.ChangeUsageChoice(settings, false));
        Assert.False(DiagnosticsConsent.Launches(settings));
        Assert.True(DiagnosticsConsent.Errors(settings));
        Assert.True(DiagnosticsConsent.ChangeUsageChoice(settings, true));
        Assert.True(DiagnosticsConsent.Metrics(settings));
        Assert.True(DiagnosticsConsent.Errors(settings));
    }

    [Fact]
    public void A_snapshot_from_before_withdrawal_cannot_be_persisted_after_reconsent()
    {
        using var folder = new Folder();
        using var reporter = new CrashReporter(Context, new Transport());
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        var generation = reporter.ConsentGeneration;
        reporter.ConfigureMetrics(folder.Path, false, false, false);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        Assert.False(reporter.RecordMetrics([new(DateTimeOffset.UtcNow, new(MetricName.FeatureUse, 1))], generation));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(folder.Path, "crash-reports"), "*.json"));
    }

    [Fact]
    public async Task Intelligence_requests_aggregate_and_interface_recording_rejects_old_consent()
    {
        using var folder = new Folder();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink);
        using var metrics = new AppMetrics(reporter);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        metrics.Configure(true);
        var previous = new IntelligenceRequestTiming(metrics);
        previous.FirstResponse();
        metrics.Configure(false);
        metrics.Configure(true);
        previous.Complete(MetricOutcome.Success);
        metrics.Flush();
        await reporter.SendPendingAsync();
        Assert.Empty(sink.Reports);
        for (var i = 0; i < 20; i++)
        {
            var request = new IntelligenceRequestTiming(metrics);
            request.FirstResponse();
            request.Complete(MetricOutcome.Success);
        }
        metrics.Flush();
        await reporter.SendPendingAsync();
        var points = Assert.IsType<MetricsReport>(Assert.Single(sink.Reports)).Points;
        Assert.Equal(20, Assert.Single(points, p => p.Observation.Name == MetricName.IntelligenceRequests).Observation.Value);
        Assert.Equal(2, points.Count(p => p.Observation.Name == MetricName.IntelligenceDuration));
        Assert.Equal(2, points.Count(p => p.Observation.Name == MetricName.IntelligenceFirstResponse));
    }

    [Fact]
    public async Task Rate_limited_memory_gauges_are_not_resampled_like_duration_streams()
    {
        using var folder = new Folder();
        var sink = new Transport();
        using var reporter = new CrashReporter(Context, sink);
        using var metrics = new AppMetrics(reporter);
        reporter.ConfigureMetrics(folder.Path, false, true, true);
        metrics.Configure(true);
        foreach (var value in new[] { 1024d, 2048d, 3072d }) metrics.Record(new(MetricName.WorkingSet, value));
        metrics.Flush();
        await reporter.SendPendingAsync();
        Assert.Equal(new[] { 1024d, 2048d, 3072d }, Assert.IsType<MetricsReport>(Assert.Single(sink.Reports)).Points.Select(p => p.Observation.Value));
    }

    private static readonly CrashReportContext Context = new("2.0.0", "linux", "x64");
    private sealed class Transport : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public Func<DiagnosticReport, CrashSendResult> Send { get; set; } = _ => new(CrashSendDisposition.Accepted);
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(Send(report));
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public ManualTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, period);
            if (dueTime == Timeout.InfiniteTimeSpan) Timer = timer;
            return timer;
        }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; private set; } = period;
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { Period = newPeriod; return true; }
        public void Tick() => callback(state);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dashynms-metrics-tests-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
