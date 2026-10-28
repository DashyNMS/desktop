using DesktopNMS.Core.Api;
using DesktopNMS.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class ApiMetricsTests
{
    [Theory]
    [InlineData(ApiRequestOutcome.Success, MetricOutcome.Success)]
    [InlineData(ApiRequestOutcome.ClientError, MetricOutcome.ClientError)]
    [InlineData(ApiRequestOutcome.ServerError, MetricOutcome.ServerError)]
    [InlineData(ApiRequestOutcome.Timeout, MetricOutcome.Timeout)]
    [InlineData(ApiRequestOutcome.NetworkFailure, MetricOutcome.NetworkFailure)]
    [InlineData(ApiRequestOutcome.Cancelled, MetricOutcome.Cancelled)]
    [InlineData(ApiRequestOutcome.Failed, MetricOutcome.Failed)]
    public async Task Outcome_counts_are_aggregated_while_durations_are_sampled(ApiRequestOutcome source, MetricOutcome expected)
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Transport.RequestObserver);
        fixture.Enable();
        for (var i = 0; i < 20; i++) fixture.Transport.RequestObserver!(new(ApiOperation.DeviceList, TimeSpan.FromMilliseconds(i), source, ApiResponseClass.None));
        fixture.Metrics.Flush();
        await fixture.Reporter.SendPendingAsync();
        var batch = Assert.IsType<MetricsReport>(Assert.Single(fixture.Sink.Reports));
        Assert.Equal(20, Assert.Single(batch.Points, p => p.Observation.Name == MetricName.ApiRequestCount).Observation.Value);
        Assert.Equal(new[] { 0d, 10d }, batch.Points.Where(p => p.Observation.Name == MetricName.ApiDuration).Select(p => p.Observation.Value));
        Assert.All(batch.Points, point => { Assert.Equal(expected, point.Observation.Outcome); Assert.Equal(MetricArea.DeviceList, point.Observation.Area); });
    }

    [Theory]
    [InlineData(ApiOperation.Alerts, MetricArea.Alerts)]
    [InlineData(ApiOperation.Sensors, MetricArea.Sensors)]
    [InlineData(ApiOperation.Other, MetricArea.ApiOther)]
    public async Task Only_fixed_operation_categories_enter_the_queue(ApiOperation source, MetricArea expected)
    {
        using var fixture = new Fixture();
        fixture.Enable();
        fixture.Transport.RequestObserver!(new(source, TimeSpan.FromMilliseconds(123), ApiRequestOutcome.Success, ApiResponseClass.Success));
        fixture.Metrics.Flush();
        await fixture.Reporter.SendPendingAsync();
        Assert.All(Assert.IsType<MetricsReport>(Assert.Single(fixture.Sink.Reports)).Points, p => Assert.Equal(expected, p.Observation.Area));
    }

    [Fact]
    public async Task An_inflight_callback_cannot_report_after_withdrawal_and_reconsent()
    {
        using var fixture = new Fixture();
        fixture.Enable();
        var previous = fixture.Transport.RequestObserver!;
        fixture.Metrics.Configure(false);
        ApiMetrics.Configure(fixture.Transport, fixture.Metrics);
        Assert.Null(fixture.Transport.RequestObserver);
        previous(new(ApiOperation.Alerts, TimeSpan.FromSeconds(1), ApiRequestOutcome.Success, ApiResponseClass.Success));
        fixture.Enable();
        previous(new(ApiOperation.Alerts, TimeSpan.FromSeconds(1), ApiRequestOutcome.Success, ApiResponseClass.Success));
        fixture.Metrics.Flush();
        await fixture.Reporter.SendPendingAsync();
        Assert.Empty(fixture.Sink.Reports);
        fixture.Transport.RequestObserver!(new(ApiOperation.Alerts, TimeSpan.FromSeconds(1), ApiRequestOutcome.Success, ApiResponseClass.Success));
        fixture.Metrics.Flush();
        await fixture.Reporter.SendPendingAsync();
        Assert.Single(fixture.Sink.Reports);
    }

    private sealed class Sink : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted));
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "dashynms-api-tests-" + Guid.NewGuid().ToString("N"));
        public Sink Sink { get; } = new();
        public CrashReporter Reporter { get; }
        public AppMetrics Metrics { get; }
        public LibreNmsTransport Transport { get; } = new(NullLogger<LibreNmsTransport>.Instance);
        public Fixture()
        {
            Reporter = new(new("2.0.0", "linux", "x64"), Sink);
            Reporter.ConfigureMetrics(_path, false, true, true);
            Metrics = new(Reporter);
            ApiMetrics.Configure(Transport, Metrics);
        }
        public void Enable() { Metrics.Configure(true); ApiMetrics.Configure(Transport, Metrics); }
        public void Dispose()
        {
            Transport.Dispose();
            Metrics.Dispose();
            Reporter.Dispose();
            Directory.Delete(_path, true);
        }
    }
}
