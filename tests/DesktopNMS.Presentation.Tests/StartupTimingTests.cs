using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class StartupTimingTests
{
    [Theory]
    [InlineData(MetricMode.Interactive)]
    [InlineData(MetricMode.Background)]
    public void Ready_excludes_network_wait_and_only_reports_once(MetricMode mode)
    {
        var clock = new Clock();
        var sink = new Sink();
        var timing = new StartupTiming(clock, () => true);
        clock.Advance(100);
        timing.PauseForSessionRestore();
        clock.Advance(10000);
        timing.Ready(sink, true, mode);
        Assert.Empty(sink.Points);
        timing.ResumeAfterSessionRestore();
        clock.Advance(50);
        timing.Ready(sink, true, mode);
        clock.Advance(1000);
        timing.Ready(sink, true, mode);
        var point = Assert.Single(sink.Points);
        Assert.Equal(MetricName.StartupDuration, point.Name);
        Assert.Equal(mode, point.Mode);
        Assert.Equal(150, point.Value);
    }

    [Fact]
    public void First_launch_consent_is_not_retroactive()
    {
        var sink = new Sink();
        var timing = new StartupTiming(new Clock(), () => true);
        timing.Ready(sink, false, MetricMode.Interactive);
        timing.Ready(sink, true, MetricMode.Interactive);
        Assert.Empty(sink.Points);
    }

    [Fact]
    public void Implausibly_long_startup_is_not_reported()
    {
        var clock = new Clock();
        var sink = new Sink();
        var timing = new StartupTiming(clock, () => true);
        clock.Advance(3600000);
        timing.Ready(sink, true, MetricMode.Interactive);
        Assert.Empty(sink.Points);
    }

    [Fact]
    public void Unsampled_startup_sends_nothing()
    {
        var sink = new Sink();
        new StartupTiming(new Clock(), () => false).Ready(sink, true, MetricMode.Interactive);
        Assert.Empty(sink.Points);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        // Wall clock intentionally unrelated to the monotonic measurement.
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
        public void Advance(long ms) => _ticks += ms;
    }
    private sealed class Sink : IAppMetrics
    {
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
}
