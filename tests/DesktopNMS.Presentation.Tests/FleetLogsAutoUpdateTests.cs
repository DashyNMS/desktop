using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class FleetLogsAutoUpdateTests
{
    [Fact]
    public void Updates_only_the_visible_log_and_stops_when_hidden_or_disabled()
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Activate();
        Assert.True(fixture.Timer.IsEnabled);
        Assert.Equal(1, fixture.EventCalls);
        fixture.Timer.Fire();
        Assert.Equal(2, fixture.EventCalls);

        vm.ShowingEventLog = false;
        fixture.Timer.Fire();
        Assert.Equal(2, fixture.EventCalls);
        Assert.Equal(2, fixture.AlertCalls);

        vm.Deactivate();
        Assert.False(fixture.Timer.IsEnabled);
        fixture.Timer.Fire(); // A queued tick is harmless too.
        Assert.Equal(2, fixture.AlertCalls);
        vm.Activate();
        vm.AutoUpdate = false;
        Assert.False(fixture.Timer.IsEnabled);
        fixture.Timer.Fire();
        Assert.Equal(2, fixture.AlertCalls);

        vm.SelectedAutoUpdateInterval = vm.AutoUpdateIntervalOptions.Last();
        Assert.Equal(TimeSpan.FromMinutes(5), fixture.Timer.Interval);
    }

    [Fact]
    public async Task Slow_refreshes_do_not_overlap_and_keep_rows_visible()
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Activate();
        var pending = new TaskCompletionSource<IReadOnlyList<EventLogEntry>>();
        fixture.Events = () => pending.Task;
        fixture.Timer.Fire();
        fixture.Timer.Fire();

        Assert.Equal(2, fixture.EventCalls);
        Assert.False(vm.IsLoading);
        Assert.Single(vm.Events);
        pending.SetResult(new[] { new EventLogEntry { Id = 2, DeviceId = 1, Message = "New event" } });
        await pending.Task;
        Assert.Equal(2, Assert.Single(vm.Events).Entry.Id);
    }

    [Fact]
    public void A_failed_auto_update_retains_rows_filters_and_selection()
    {
        var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.Activate();
        vm.SearchText = "example";
        vm.SelectedEvent = vm.Events.Single();
        fixture.Events = () => Task.FromException<IReadOnlyList<EventLogEntry>>(new LibreNmsApiException("Example server unavailable"));

        fixture.Timer.Fire();

        Assert.Single(vm.Events);
        Assert.NotNull(vm.SelectedEvent);
        Assert.Equal("example", vm.SearchText);
        Assert.False(vm.HasError); // No blocking error overlay over the old rows.
        Assert.Contains("showing entries from", vm.LastUpdatedText);
        fixture.Events = () => Task.FromResult<IReadOnlyList<EventLogEntry>>(new[] { new EventLogEntry { Id = 1, DeviceId = 1, Message = "example event" } });
        fixture.Timer.Fire();
        Assert.StartsWith("Updated ", vm.LastUpdatedText);
        Assert.NotNull(vm.SelectedEvent);
    }

    private sealed class Fixture
    {
        public Timer Timer { get; } = new();
        public int EventCalls;
        public int AlertCalls;
        public Func<Task<IReadOnlyList<EventLogEntry>>> Events = () => Task.FromResult<IReadOnlyList<EventLogEntry>>(
            new[] { new EventLogEntry { Id = 1, DeviceId = 1, Message = "example event" } });
        public FleetLogsViewModel ViewModel { get; }

        public Fixture()
        {
            var logs = Proxy<ILogsApi>((method, _) => method.Name switch
            {
                nameof(ILogsApi.ListEventLogAsync) => EventRequest(),
                nameof(ILogsApi.ListAlertLogAsync) => AlertRequest(),
                _ => throw new NotSupportedException(method.Name),
            });
            var rules = Proxy<IAlertRulesApi>((_, _) => Task.FromResult<IReadOnlyList<AlertRule>>(Array.Empty<AlertRule>()));
            var client = Proxy<ILibreNmsClient>((method, _) => method.Name == "get_Logs" ? logs : rules);
            var cache = Proxy<IDeviceCache>((_, _) => null);
            var settings = Proxy<ISettingsStore>((_, _) => new AppSettings());
            ViewModel = new(client, cache, settings, null!, NullLogger.Instance, Timer);
        }

        private Task<IReadOnlyList<EventLogEntry>> EventRequest() { EventCalls++; return Events(); }
        private Task<IReadOnlyList<AlertLogEntry>> AlertRequest() { AlertCalls++; return Task.FromResult<IReadOnlyList<AlertLogEntry>>(Array.Empty<AlertLogEntry>()); }
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class Timer : IUiTimer
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
