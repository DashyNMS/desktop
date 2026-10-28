using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class HealthRefreshTests
{
    [Fact]
    public void Failed_initial_load_ends_the_loading_state_and_a_later_timeout_keeps_readings()
    {
        var settings = new AppSettings();
        var store = Proxy<ISettingsStore>((method, _) => method.Name == "get_Current" ? settings : null);
        var session = Proxy<ISessionService>((method, _) => method.Name == "get_IsConnected" ? true : null);
        var activity = Proxy<IAppActivity>((method, _) => method.Name == "get_IsInBackground" ? false : null);
        var cache = Proxy<IDeviceCache>((_, _) => null);
        using var monitor = new SensorMonitor(null!, session, store, cache, null!, activity, NullLogger<SensorMonitor>.Instance);
        using var health = new HealthViewModel(monitor, session, store, cache, null!);

        health.ApplyPollResult(Failed());
        Assert.False(health.Temperature.LoadState.IsLoading);
        Assert.Contains("Try refreshing again", health.ErrorMessage);

        var observed = DateTimeOffset.Now.AddMinutes(-5);
        health.ApplyPollResult(new SensorPollResult
        {
            Succeeded = true, CompletedAt = observed, ObservedAt = observed,
            Sensors = new[] { new Sensor { SensorId = 1, DeviceId = 1, SensorClass = "temperature", Current = 24.5 } },
        });
        var row = Assert.Single(health.Temperature.Sensors);
        health.ApplyPollResult(Failed(observed));

        Assert.Same(row, Assert.Single(health.Temperature.Sensors));
        Assert.False(health.Temperature.LoadState.IsLoading);
        Assert.Contains(observed.LocalDateTime.ToString("HH:mm"), health.ErrorMessage);
        Assert.StartsWith("LibreNMS took too long", health.ErrorMessage);
        Assert.Equal("Updating…", health.RefreshText);
    }

    [Fact]
    public void Cached_health_visit_reports_once_while_background_results_do_not()
    {
        var settings = new AppSettings();
        var store = Proxy<ISettingsStore>((method, _) => method.Name == "get_Current" ? settings : null);
        var session = Proxy<ISessionService>((method, _) => method.Name == "get_IsConnected" ? true : null);
        var activity = Proxy<IAppActivity>((method, _) => method.Name == "get_IsInBackground" ? false : null);
        var cache = Proxy<IDeviceCache>((_, _) => null);
        using var monitor = new SensorMonitor(null!, session, store, cache, null!, activity, NullLogger<SensorMonitor>.Instance);
        var sink = new MetricsSink();
        using var health = new HealthViewModel(monitor, session, store, cache, null!, sink);
        health.ApplyPollResult(new SensorPollResult { Succeeded = true, CompletedAt = DateTimeOffset.Now, Sensors = [] });
        Assert.Empty(sink.Points);
        health.OnShown();
        var point = Assert.Single(sink.Points);
        Assert.Equal(DesktopNMS.Diagnostics.MetricOutcome.Cached, point.Outcome);
        Assert.Equal(DesktopNMS.Diagnostics.MetricArea.Health, point.Area);
        health.ApplyPollResult(Failed());
        Assert.Single(sink.Points);
    }

    private sealed class MetricsSink : DesktopNMS.Diagnostics.IAppMetrics
    {
        public List<DesktopNMS.Diagnostics.MetricObservation> Points { get; } = [];
        public void Record(DesktopNMS.Diagnostics.MetricObservation observation) => Points.Add(observation);
    }

    private static SensorPollResult Failed(DateTimeOffset? observed = null) => new()
    {
        Succeeded = false, CompletedAt = DateTimeOffset.Now, ObservedAt = observed, IsTimeout = true, Sensors = Array.Empty<Sensor>(),
    };

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>(); ((Stub)(object)proxy).Handler = handler; return proxy;
    }
}
