using DesktopNMS.Core.Services;
using DesktopNMS.Diagnostics;
using DesktopNMS.Infrastructure;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class UiStallMonitorTests
{
    [Fact]
    public async Task Delayed_heartbeats_aggregate_counts_and_sample_durations()
    {
        using var f = new Fixture();
        for (var i = 0; i < 20; i++)
        {
            f.Tick();
            f.Clock.Advance(TimeSpan.FromMilliseconds(600));
            f.Dispatcher.Run();
        }
        var points = await f.Points();
        Assert.Equal(20, Assert.Single(points, p => p.Observation.Name == MetricName.UiStallCount).Observation.Value);
        var durations = points.Where(p => p.Observation.Name == MetricName.UiStallDuration).ToArray();
        Assert.Equal(2, durations.Length);
        Assert.All(durations, p => Assert.Equal(600, p.Observation.Value));
    }

    [Fact]
    public async Task A_blocked_dispatcher_never_accumulates_more_than_one_callback()
    {
        using var f = new Fixture();
        for (var i = 0; i < 100; i++) f.Tick();
        Assert.Single(f.Dispatcher.Pending);
        f.Dispatcher.Run();
        Assert.Equal(1, Assert.Single(await f.Points(), p => p.Observation.Name == MetricName.UiStallCount).Observation.Value);
    }

    [Theory]
    [InlineData("background")]
    [InlineData("debugger")]
    [InlineData("worker-gap")]
    [InlineData("sleep")]
    [InlineData("clock-jump")]
    [InlineData("withdrawal")]
    [InlineData("hidden-at-ack")]
    public async Task Excluded_intervals_are_not_reported_as_freezes(string reason)
    {
        using var f = new Fixture();
        f.Tick();
        f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        switch (reason)
        {
            case "background": f.Activity.Background(true); break;
            case "debugger": f.Debugging = true; break;
            case "worker-gap": f.Clock.Advance(TimeSpan.FromSeconds(10)); break;
            case "sleep": f.Clock.Wall += TimeSpan.FromHours(1); break;
            case "clock-jump": f.Clock.Wall -= TimeSpan.FromHours(1); break;
            case "withdrawal":
                f.Metrics.Configure(false);
                f.Monitor.Configure();
                f.Metrics.Configure(true);
                f.Monitor.Configure();
                break;
            case "hidden-at-ack": f.Visible = false; break;
        }
        f.Dispatcher.Run();
        Assert.Empty(await f.Points());
    }

    [Fact]
    public async Task Fast_acknowledgements_and_disabled_collection_produce_no_measurement()
    {
        using var f = new Fixture();
        f.Tick();
        f.Clock.Advance(TimeSpan.FromMilliseconds(100));
        f.Dispatcher.Run();
        Assert.Empty(await f.Points());
        f.Metrics.Configure(false);
        f.Monitor.Configure();
        f.Tick();
        Assert.Empty(f.Dispatcher.Pending);
    }

    [Fact]
    public async Task Background_ticks_do_not_add_UI_work()
    {
        using var f = new Fixture();
        f.Activity.Background(true);
        for (var i = 0; i < 100; i++) f.Tick();
        Assert.Empty(f.Dispatcher.Pending);
        Assert.Empty(await f.Points());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "dashynms-stall-tests-" + Guid.NewGuid().ToString("N"));
        private readonly Sink _sink = new();
        private readonly CrashReporter _reporter;
        public Clock Clock { get; } = new();
        public Activity Activity { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        public AppMetrics Metrics { get; }
        public UiStallMonitor Monitor { get; }
        public bool Debugging, Visible = true;
        public Fixture()
        {
            _reporter = new(new("2.0.0", "linux", "x64"), _sink, Clock);
            _reporter.ConfigureMetrics(_path, false, true, true);
            Metrics = new(_reporter, Clock);
            Metrics.Configure(true);
            Monitor = new(Metrics, Activity, Dispatcher, Clock, () => Debugging, () => Visible && !Activity.IsInBackground);
            Monitor.Configure();
        }
        public void Tick()
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var timer in Clock.Timers.Where(t => t.Period == TimeSpan.FromSeconds(1)).ToArray()) timer.Tick();
        }
        public async Task<IReadOnlyList<MetricPoint>> Points()
        {
            Metrics.Flush();
            await _reporter.SendPendingAsync();
            return _sink.Reports.OfType<MetricsReport>().SelectMany(r => r.Points).ToArray();
        }
        public void Dispose() { Monitor.Dispose(); Metrics.Dispose(); _reporter.Dispose(); Directory.Delete(_path, true); }
    }
    private sealed class Sink : ICrashTransport
    {
        public bool IsAvailable => true;
        public List<DiagnosticReport> Reports { get; } = [];
        public Task<CrashSendResult> SendAsync(DiagnosticReport report, CancellationToken cancellationToken)
        { Reports.Add(report); return Task.FromResult(new CrashSendResult(CrashSendDisposition.Accepted)); }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset Wall = DateTimeOffset.UtcNow;
        public List<Timer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Wall;
        public void Advance(TimeSpan by) { _ticks += by.Ticks; Wall += by; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, period); Timers.Add(timer); return timer; }
    }
    private sealed class Timer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period = period;
        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { Period = newPeriod; return true; }
        public void Tick() => callback(state);
        public void Dispose() => Period = Timeout.InfiniteTimeSpan;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Activity : IAppActivity
    {
        public bool IsInBackground { get; private set; }
        public event EventHandler? Changed;
        public void Background(bool value) { IsInBackground = value; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public Queue<Action> Pending { get; } = new();
        public bool CheckAccess() => false;
        public void Post(Action action, UiPriority priority = UiPriority.Normal)
        { Assert.Equal(UiPriority.Normal, priority); Pending.Enqueue(action); }
        public void Run() => Pending.Dequeue()();
        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal) => throw new NotSupportedException();
        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => throw new NotSupportedException();
    }
}
