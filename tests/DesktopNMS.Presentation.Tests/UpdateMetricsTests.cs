using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class UpdateMetricsTests
{
    [Fact]
    public void Marker_is_minimal_bounded_and_withdrawal_purges_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dashynms-update-metrics-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var metrics = new UpdateMetrics(sink, folder, "2.0.0+build");
        try
        {
            var install = metrics.Begin(MetricArea.UpdateInstall, "v2.1.0-preview.1", true, MetricMode.Manual);
            metrics.MarkPending(install);
            var path = Assert.Single(Directory.GetFiles(folder));
            Assert.True(new FileInfo(path).Length <= 1024);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(folder, text);
            Assert.DoesNotContain("build", text);
            metrics.Withdraw();
            Assert.Empty(Directory.GetFiles(folder));
            metrics.ConfirmStartup("2.1.0-preview.1");
            Assert.Empty(sink.Points);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Pending_and_completed_operations_do_not_survive_withdrawal_reconsent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dashynms-update-metrics-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var metrics = new UpdateMetrics(sink, folder, "2.0.0");
        var operation = metrics.Begin(MetricArea.UpdateInstall, "2.1.0", false, MetricMode.Manual);
        sink.ConsentGeneration++;
        metrics.MarkPending(operation);
        operation.Complete(MetricOutcome.Launched);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(sink.Points);
    }

    [Fact]
    public void Different_running_version_is_not_reported_as_install_failure_or_confirmation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dashynms-update-metrics-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var metrics = new UpdateMetrics(sink, folder, "2.0.0");
        try
        {
            metrics.MarkPending(metrics.Begin(MetricArea.UpdateInstall, "2.1.0", false, MetricMode.Manual));
            metrics.ConfirmStartup("2.2.0");
            Assert.Equal(MetricOutcome.DifferentVersion, Assert.Single(sink.Points).Outcome);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact]
    public void A_manual_or_package_manager_version_change_needs_no_install_marker()
    {
        var sink = new Sink();
        var metrics = new UpdateMetrics(sink, "unused", "2.1.0");
        metrics.ConfirmUntrackedStartup(null, "2.1.0");
        metrics.ConfirmUntrackedStartup("2.1.0", "2.1.0");
        Assert.Empty(sink.Points);
        metrics.ConfirmUntrackedStartup("2.0.0", "2.1.0");
        var point = Assert.Single(sink.Points);
        Assert.Equal(MetricMode.Untracked, point.Mode);
        Assert.Equal(MetricOutcome.Confirmed, point.Outcome);
        Assert.Equal("2.0.0", point.Update!.Source);
        Assert.Equal("2.1.0", point.Update.Target);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("expired")]
    [InlineData("malformed")]
    public void Invalid_recovery_state_is_purged_without_confirmation(string kind)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dashynms-update-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "pending-update-metrics.json");
        var sink = new Sink();
        try
        {
            File.WriteAllText(path, kind switch
            {
                "oversized" => new string('x', 2048),
                "expired" => System.Text.Json.JsonSerializer.Serialize(new { Schema = 1, Timestamp = DateTimeOffset.UtcNow.AddDays(-8), Context = new UpdateMetricContext("2.0.0", "2.1.0", MetricUpdateChannel.Stable) }),
                _ => "invalid-json",
            });
            Assert.False(new UpdateMetrics(sink, folder, "2.1.0").ConfirmStartup("2.1.0"));
            Assert.Empty(sink.Points);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Sink : IAppMetrics
    {
        public long ConsentGeneration { get; set; }
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
}
