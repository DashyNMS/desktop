using DesktopNMS.Diagnostics;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class ScreenLoadTimingTests
{
    [Theory]
    [InlineData(MetricOutcome.Success)]
    [InlineData(MetricOutcome.Failed)]
    [InlineData(MetricOutcome.Cancelled)]
    public void A_visible_visit_has_one_outcome_even_with_overlapping_starts_and_refreshes(MetricOutcome outcome)
    {
        var clock = new Clock();
        var sink = new Sink();
        var timing = new ScreenLoadTiming(sink, MetricArea.Health, clock);
        timing.Complete(MetricOutcome.Success); // Background poll before visiting.
        timing.Begin(false);
        clock.Advance(50);
        timing.Begin(false); // Overlapping refresh must not reset the clock.
        clock.Advance(100);
        timing.Complete(outcome);
        timing.Complete(MetricOutcome.Success); // Duplicate callback.
        var point = Assert.Single(sink.Points);
        Assert.Equal(150, point.Value);
        Assert.Equal(outcome, point.Outcome);
        Assert.Equal(MetricArea.Health, point.Area);
        Assert.Equal(MetricName.ScreenLoadDuration, point.Name);
    }

    [Fact]
    public void Cached_visit_is_distinct_and_later_polls_do_not_count()
    {
        var sink = new Sink();
        var timing = new ScreenLoadTiming(sink, MetricArea.Dashboard, new Clock());
        timing.Begin(true);
        timing.Complete(MetricOutcome.Success);
        Assert.Equal(MetricOutcome.Cached, Assert.Single(sink.Points).Outcome);
        timing.Begin(true); // A new visible visit is eligible.
        Assert.Equal(2, sink.Points.Count);
    }

    [Fact]
    public void Enabling_consent_during_a_load_does_not_retroactively_measure_it()
    {
        var sink = new Sink { IsEnabled = false };
        var timing = new ScreenLoadTiming(sink, MetricArea.Alerts, new Clock());
        timing.Begin(false);
        sink.IsEnabled = true;
        timing.Complete(MetricOutcome.Success);
        Assert.Empty(sink.Points);
    }

    [Fact]
    public void Withdrawal_and_reconsent_cannot_revive_an_old_pending_observation()
    {
        var sink = new Sink();
        var timing = new ScreenLoadTiming(sink, MetricArea.DeviceDetails, new Clock());
        timing.Begin(false);
        sink.ConsentGeneration++;
        timing.Complete(MetricOutcome.Success);
        Assert.Empty(sink.Points);
        timing.Begin(false);
        timing.Complete(MetricOutcome.Success);
        Assert.Single(sink.Points);
    }

    [Fact]
    public void Implausibly_long_loads_are_discarded()
    {
        var clock = new Clock();
        var sink = new Sink();
        var timing = new ScreenLoadTiming(sink, MetricArea.Alerts, clock);
        timing.Begin(false);
        clock.Advance(3600000); // Sleep/debugger outlier.
        timing.Complete(MetricOutcome.Success);
        Assert.Empty(sink.Points);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(long ms) => _timestamp += ms;
    }
    private sealed class Sink : IAppMetrics
    {
        public bool IsEnabled { get; set; } = true;
        public long ConsentGeneration { get; set; }
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
}
