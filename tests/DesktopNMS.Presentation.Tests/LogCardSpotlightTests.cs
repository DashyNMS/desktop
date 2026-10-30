using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Spotlights;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>The Event log and Graylog cards' Spotlight (#552).</summary>
public sealed class LogCardSpotlightTests
{
    private static readonly Device[] Devices =
    {
        new() { DeviceId = 1, Hostname = "core-01.example.net", Ip = "192.0.2.1" },
        new() { DeviceId = 2, Hostname = "core-02.example.net", Ip = "192.0.2.2" },
        new() { DeviceId = 3, Hostname = "edge-01.example.net", Ip = "198.51.100.1" },
    };

    [Fact]
    public async Task A_card_with_no_Spotlight_reads_the_whole_network()
    {
        var fixture = new Fixture();
        using var card = fixture.EventLog();
        await fixture.Settled();

        Assert.False(card.HasSpotlight);
        Assert.Equal(new int?[] { null }, fixture.EventLogCalls);
        Assert.Equal("Spotlight devices, a group or a location", card.SpotlightToolTip);
    }

    [Fact]
    public async Task Spotlighting_a_device_saves_it_and_reads_only_its_log()
    {
        var fixture = new Fixture();
        using var card = fixture.EventLog();
        await fixture.Settled();
        fixture.EventLogCalls.Clear();

        Assert.True(card.AddSpotlight(new SpotlightOption(SpotlightKind.Device, 3, "edge-01")));
        await fixture.Settled();

        var saved = Assert.Single(fixture.Widget.Spotlight);
        Assert.Equal((SpotlightKind.Device, 3), (saved.Kind!.Value, saved.Id));
        Assert.Equal(new int?[] { 3 }, fixture.EventLogCalls);
        Assert.Equal("edge-01", card.SpotlightSummary);
        Assert.Equal("Spotlight: edge-01", card.SpotlightToolTip);
        Assert.All(card.Rows, row => Assert.Equal(3, row.DeviceId));
    }

    [Fact]
    public async Task Clearing_the_Spotlight_goes_back_to_the_whole_network()
    {
        var fixture = new Fixture();
        fixture.Widget.Spotlight.Add(SpotlightTarget.Device(1, "core-01"));
        using var card = fixture.EventLog();
        await fixture.Settled();

        card.ClearSpotlightCommand.Execute(null);
        await fixture.Settled();

        Assert.Empty(fixture.Widget.Spotlight);
        Assert.Equal("Whole network", SpotlightScope.Describe(fixture.Widget.Spotlight));
        Assert.Null(fixture.EventLogCalls.Last());
    }

    [Fact]
    public async Task A_group_takes_its_current_name_and_a_removed_one_is_called_unknown()
    {
        var fixture = new Fixture();
        fixture.Widget.Spotlight.Add(SpotlightTarget.Group(5, "Old name"));
        fixture.Widget.Spotlight.Add(SpotlightTarget.Group(6, "Retired"));
        using var card = fixture.EventLog();
        await fixture.Settled();

        Assert.Equal("Core routers, Unknown group (Retired)", card.SpotlightSummary);
        Assert.Equal(new int?[] { 1, 2 }, fixture.EventLogCalls.OrderBy(id => id));

        // The removed group stays saved rather than quietly widening the card.
        Assert.Equal(2, fixture.Widget.Spotlight.Count);
    }

    [Fact]
    public async Task Graylog_searches_only_the_Spotlights_devices()
    {
        var fixture = new Fixture();
        fixture.Widget.WidgetType = DashboardWidgetTypes.Graylog;
        fixture.Widget.LogSearch = "level:<=3";
        fixture.Widget.Spotlight.Add(SpotlightTarget.Group(5, "Core routers"));
        using var card = fixture.Graylog();
        await fixture.Settled();

        var query = Assert.Single(fixture.GraylogQueries);
        Assert.StartsWith("(level:<=3) AND source: (", query);
        Assert.Contains("\"core-01.example.net\"", query);
        Assert.Contains("\"192.0.2.2\"", query);
        Assert.DoesNotContain("edge-01", query);
        Assert.Equal("No messages from Core routers in the last 15 min.", card.EmptyText);
    }

    [Fact]
    public async Task Entries_a_newer_app_added_are_kept_when_the_Spotlight_changes()
    {
        var fixture = new Fixture();
        fixture.Widget.Spotlight.Add(new SpotlightTarget { KindName = "tenant", Id = 9, Name = "Newer app" });
        using var card = fixture.EventLog();
        await fixture.Settled();

        Assert.False(card.HasSpotlight);
        card.AddSpotlight(new SpotlightOption(SpotlightKind.Device, 1, "core-01"));

        Assert.Equal(new[] { "device", "tenant" }, fixture.Widget.Spotlight.Select(t => t.KindName));
    }

    [Fact]
    public async Task Only_devices_groups_and_locations_can_be_spotlighted_and_each_once()
    {
        var fixture = new Fixture();
        using var card = fixture.EventLog();
        await fixture.Settled();

        Assert.False(card.AddSpotlight(new SpotlightOption(SpotlightKind.Port, 4, "Gi0/1")));
        Assert.True(card.AddSpotlight(new SpotlightOption(SpotlightKind.Location, 7, "London")));
        Assert.True(card.AddSpotlight(new SpotlightOption(SpotlightKind.Location, 7, "London")));

        Assert.Single(card.Spotlight);
    }

    private sealed class Fixture
    {
        private readonly AppSettings _settings = new();
        private readonly SettingsStoreStub _store;
        private readonly ILibreNmsClient _client;
        private readonly IDeviceCache _devices;
        private readonly DeviceMonitor _monitor;
        private readonly DashboardLayoutService _layout;
        private readonly SpotlightResolver _resolver;

        public Fixture()
        {
            Widget = new DashboardWidget { Id = "card", WidgetType = DashboardWidgetTypes.EventLog };
            _settings.DashboardWidgets.Add(Widget);
            _store = new SettingsStoreStub(_settings);
            _layout = new DashboardLayoutService(_store);

            var logs = Proxy<ILogsApi>((method, args) =>
            {
                if (method.Name != nameof(ILogsApi.ListEventLogAsync))
                {
                    throw new NotSupportedException(method.Name);
                }

                var deviceId = (int?)args![0];
                lock (EventLogCalls)
                {
                    EventLogCalls.Add(deviceId);
                }

                var entries = Devices
                    .Where(d => deviceId is null || d.DeviceId == deviceId)
                    .Select(d => new EventLogEntry { Id = d.DeviceId * 10, DeviceId = d.DeviceId, Message = "example event", Timestamp = new DateTime(2026, 10, 30, 9, d.DeviceId, 0) })
                    .ToArray();
                return Task.FromResult<IReadOnlyList<EventLogEntry>>(entries);
            });
            var groups = Proxy<IDeviceGroupsApi>((method, args) => method.Name switch
            {
                nameof(IDeviceGroupsApi.ListAsync) => Task.FromResult<IReadOnlyList<DeviceGroup>>(new[] { new DeviceGroup { Id = 5, Name = "Core routers" } }),
                nameof(IDeviceGroupsApi.GetMemberDeviceIdsAsync) => Task.FromResult<IReadOnlyList<int>>(new[] { 1, 2 }),
                _ => throw new NotSupportedException(method.Name),
            });
            var locations = Proxy<ILocationsApi>((_, _) => Task.FromResult<IReadOnlyList<Location>>(new[] { new Location { Id = 7, Name = "London" } }));
            _client = Proxy<ILibreNmsClient>((method, _) => method.Name switch
            {
                "get_Logs" => logs,
                "get_DeviceGroups" => groups,
                "get_Locations" => locations,
                _ => throw new NotSupportedException(method.Name),
            });
            _devices = Proxy<IDeviceCache>((method, args) => method.Name switch
            {
                "get_IsLoaded" => true,
                "get_All" => Devices,
                nameof(IDeviceCache.Get) => Devices.FirstOrDefault(d => d.DeviceId == (int)args![0]!),
                nameof(IDeviceCache.FindByAddress) => null,
                nameof(IDeviceCache.EnsureCurrentAsync) => Task.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            });

            var offline = Proxy<IConnectionState>((_, _) => false);
            var activity = Proxy<IAppActivity>((method, _) => method.Name == "get_IsInBackground" ? false : null);
            var alerts = new AlertMonitor(_client, offline, _store, null!, _devices, NullLogger<AlertMonitor>.Instance);
            _monitor = new DeviceMonitor(_client, offline, _store, _devices, alerts, activity, NullLogger<DeviceMonitor>.Instance);
            _resolver = new SpotlightResolver(_client);
        }

        public DashboardWidget Widget { get; }

        public List<int?> EventLogCalls { get; } = new();

        public List<string> GraylogQueries { get; } = new();

        public EventLogWidgetViewModel EventLog()
            => new(_layout, Widget, _client, _devices, _store, _monitor, null!, _resolver, NullLogger.Instance);

        public GraylogWidgetViewModel Graylog()
        {
            var graylog = Proxy<IGraylogApi>((method, args) => method.Name switch
            {
                "get_IsConfigured" => true,
                "add_ConfigurationChanged" or "remove_ConfigurationChanged" => null,
                nameof(IGraylogApi.GetStreamsAsync) => Task.FromResult<IReadOnlyList<GraylogStream>>(Array.Empty<GraylogStream>()),
                nameof(IGraylogApi.SearchAsync) => Search((string)args![0]!),
                _ => throw new NotSupportedException(method.Name),
            });
            return new GraylogWidgetViewModel(_layout, Widget, graylog, _client, _devices, _store, _monitor, null!, _resolver, NullLogger.Instance);
        }

        /// <summary>Everything here completes at once, but through a few awaits - let them run.</summary>
        public async Task Settled()
        {
            for (var i = 0; i < 5; i++)
            {
                await Task.Yield();
            }
        }

        private Task<GraylogSearchResult> Search(string query)
        {
            GraylogQueries.Add(query);
            return Task.FromResult(new GraylogSearchResult());
        }
    }

    private sealed class SettingsStoreStub : ISettingsStore
    {
        public SettingsStoreStub(AppSettings settings) => Current = settings;

        public AppSettings Current { get; private set; }

        public event EventHandler<AppSettings>? Changed;

        public AppSettings Load() => Current;

        public void Save() => Changed?.Invoke(this, Current);

        public void SaveQuietly()
        {
        }

        public void Replace(AppSettings settings) => Current = settings;
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
}
