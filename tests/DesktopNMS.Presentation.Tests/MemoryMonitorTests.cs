using System.Diagnostics;
using DesktopNMS.Core.Services;
using DesktopNMS.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace DesktopNMS.Presentation.Tests;

public sealed class MemoryMonitorTests(ITestOutputHelper output)
{
    [Fact]
    public void Current_process_snapshot_is_available_without_enumerating_other_processes()
    {
        var start = Stopwatch.GetTimestamp();
        var snapshot = MemoryMonitor.ReadCurrent();
        output.WriteLine($"Current-process snapshot duration: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} ms");
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.ManagedHeapBytes >= 0);
    }

    [Fact]
    public void Reads_are_rate_limited_and_modes_are_separate()
    {
        var clock = new Clock();
        var sink = new Sink();
        var activity = new Activity();
        var reads = 0;
        using var monitor = new MemoryMonitor(sink, activity, clock, () => { reads++; return new(4096, 2048); });
        monitor.Configure();
        clock.Advance(TimeSpan.FromMinutes(4)); clock.Tick();
        Assert.Equal(0, reads);
        clock.Advance(TimeSpan.FromMinutes(1)); clock.Tick(); clock.Tick();
        Assert.Equal(1, reads);
        Assert.Equal(2, sink.Points.Count);
        Assert.All(sink.Points, p => Assert.Equal(MetricMode.Interactive, p.Mode));
        activity.IsInBackground = true;
        clock.Advance(TimeSpan.FromMinutes(5)); clock.Tick();
        Assert.Equal(2, reads);
        Assert.Equal(4, sink.Points.Count);
        Assert.All(sink.Points.Skip(2), p => Assert.Equal(MetricMode.Background, p.Mode));
        Assert.Equal(new[] { MetricName.WorkingSet, MetricName.ManagedHeap }, sink.Points.Take(2).Select(p => p.Name));
    }

    [Fact]
    public void Withdrawal_during_a_read_cannot_emit_under_a_new_consent_generation()
    {
        var clock = new Clock();
        var sink = new Sink();
        using var monitor = new MemoryMonitor(sink, new Activity(), clock, () => { sink.ConsentGeneration++; return new(4096, 2048); });
        monitor.Configure();
        clock.Advance(TimeSpan.FromMinutes(5)); clock.Tick();
        Assert.Empty(sink.Points);
        sink.IsEnabled = false; monitor.Configure();
        clock.Advance(TimeSpan.FromMinutes(5)); clock.Tick();
        Assert.Empty(sink.Points);
    }

    [Fact]
    public void A_failed_read_does_not_report_an_error_or_retry_in_a_burst()
    {
        var clock = new Clock();
        var sink = new Sink(); var reads = 0;
        using var monitor = new MemoryMonitor(sink, new Activity(), clock, () => { reads++; throw new InvalidOperationException(); });
        monitor.Configure();
        clock.Advance(TimeSpan.FromMinutes(5)); clock.Tick(); clock.Tick();
        Assert.Equal(1, reads);
        Assert.Empty(sink.Points);
    }

    private sealed class Sink : IAppMetrics
    {
        public bool IsEnabled { get; set; } = true;
        public long ConsentGeneration { get; set; }
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
    private sealed class Activity : IAppActivity
    {
        public bool IsInBackground { get; set; }
        public event EventHandler? Changed { add { } remove { } }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private Timer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public void Tick() => _timer?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new Timer(callback, state);
    }
    private sealed class Timer(TimerCallback callback, object? state) : ITimer
    {
        private bool _enabled;
        public bool Change(TimeSpan dueTime, TimeSpan period) { _enabled = period != Timeout.InfiniteTimeSpan; return true; }
        public void Tick() { if (_enabled) callback(state); }
        public void Dispose() => _enabled = false;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
