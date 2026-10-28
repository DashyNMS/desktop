using DesktopNMS.Core.Configuration;
using DesktopNMS.Diagnostics;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class FeatureMetricsTests
{
    [Theory]
    [InlineData(MainTab.MapsNetwork, MetricArea.Maps)]
    [InlineData(MainTab.MapsGeographical, MetricArea.Maps)]
    [InlineData(MainTab.MapsCustom, MetricArea.Maps)]
    [InlineData(MainTab.Intelligence, MetricArea.Intelligence)]
    public void Only_a_changed_feature_tab_counts(MainTab tab, MetricArea area)
    {
        var sink = new Sink();
        var metrics = new FeatureMetrics(sink);
        metrics.Navigated(tab, tab);
        metrics.Navigated(MainTab.Alerts, MainTab.Devices);
        Assert.Empty(sink.Points);
        metrics.Navigated(MainTab.Alerts, tab);
        var point = Assert.Single(sink.Points);
        Assert.Equal(MetricName.FeatureUse, point.Name);
        Assert.Equal(area, point.Area);
        Assert.Equal(1, point.Value);
    }

    [Fact]
    public void Every_built_in_widget_maps_to_a_fixed_category_and_unknown_text_is_discarded()
    {
        var sink = new Sink();
        var metrics = new FeatureMetrics(sink);
        metrics.WidgetAdded("private-widget-canary");
        metrics.WidgetAdded(null);
        Assert.Empty(sink.Points);
        foreach (var type in DashboardWidgetTypes.Known) metrics.WidgetAdded(type);
        Assert.Equal(DashboardWidgetTypes.Known.Count, sink.Points.Count);
        Assert.Equal(sink.Points.Count, sink.Points.Select(p => p.Area).Distinct().Count());
        Assert.All(sink.Points, point => Assert.StartsWith("Widget", point.Area.ToString()));
    }

    [Fact]
    public async Task Counts_aggregate_locally_and_withdrawal_stops_further_collection()
    {
        var path = Path.Combine(Path.GetTempPath(), "dashynms-feature-tests-" + Guid.NewGuid().ToString("N"));
        var sink = new Transport();
        using var reporter = new CrashReporter(new("2.0.0", "linux", "x64"), sink);
        using var buffer = new AppMetrics(reporter);
        reporter.ConfigureMetrics(path, false, true, true);
        var metrics = new FeatureMetrics(buffer);
        try
        {
            metrics.EnteredDashboardEdit(); // Before consent.
            buffer.Configure(true);
            for (var i = 0; i < 20; i++)
            {
                metrics.Navigated(MainTab.Alerts, MainTab.MapsNetwork);
                metrics.WidgetAdded(DashboardWidgetTypes.Sensors);
            }
            metrics.EnteredDashboardEdit();
            buffer.Flush();
            await reporter.SendPendingAsync();
            var points = Assert.IsType<MetricsReport>(Assert.Single(sink.Reports)).Points;
            Assert.Equal(3, points.Count);
            Assert.Equal(20, Assert.Single(points, p => p.Observation.Area == MetricArea.Maps).Observation.Value);
            Assert.Equal(20, Assert.Single(points, p => p.Observation.Area == MetricArea.WidgetSensors).Observation.Value);
            Assert.Equal(1, Assert.Single(points, p => p.Observation.Area == MetricArea.DashboardEditor).Observation.Value);
            buffer.Configure(false);
            metrics.EnteredDashboardEdit();
            buffer.Configure(true);
            buffer.Flush();
            await reporter.SendPendingAsync();
            Assert.Single(sink.Reports);
        }
        finally { buffer.Configure(false); Directory.Delete(path, true); }
    }
    private sealed class Sink : IAppMetrics
    {
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
    private sealed class Transport : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        { Reports.Add(report); return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted)); }
    }
}
