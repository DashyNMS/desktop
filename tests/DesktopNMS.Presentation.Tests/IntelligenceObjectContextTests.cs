using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class IntelligenceObjectContextTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(null)]
    public void Top_widget_rows_keep_the_specific_interface_or_device(int? port)
    {
        var row = new TopRowViewModel(12, port, "Switch", "Ethernet1", "1 Mbps", "2 Mbps", .2, DeviceState.Up);
        var target = IntelligenceObjectTargets.From(row)!;
        Assert.Equal(port is { } id ? new("port", id, 12) : new IntelligenceObjectReference("device", 12), target.Identity);
        if (port is not null) Assert.Contains("port_id=42, device_id=12", target.AgentDescription);
    }

    [Fact]
    public async Task Interface_scope_rechecks_the_exact_port_and_parent_device()
    {
        var ports = Proxy<IPortsApi>((_, _) => Task.FromResult<IReadOnlyList<Port>>(new[] { new Port { PortId = 42, DeviceId = 12, IfName = "Ethernet1" } }));
        var context = Context(Proxy<ILibreNmsClient>((_, _) => ports));
        Assert.NotNull(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("port", 42, 12), "Interface")!, default));
        Assert.Null(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("port", 42, 99), "Wrong parent")!, default));
        Assert.Null(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("port", 43, 12), "Deleted")!, default));
    }

    [Fact]
    public void Other_dashboard_rows_offer_device_scope_without_guessing_log_sources()
    {
        var pinned = new PinnedDeviceItemViewModel(new PinnedDevice { DeviceId = 12, DisplayName = "Switch" }, _ => { }, _ => { });
        var recent = new RecentlyViewedDeviceItemViewModel(new RecentlyViewedDevice { DeviceId = 12, DisplayName = "Switch" }, _ => { });
        var log = new LogFeedRowViewModel("entry", "now", "now", "Switch", null, "Event", 12, null);
        foreach (var row in new object[] { pinned, recent, log, new DevicePickerItem(12, "Switch") })
            Assert.Equal(new IntelligenceObjectReference("device", 12), IntelligenceObjectTargets.From(row)!.Identity);
        Assert.Null(IntelligenceObjectTargets.From(new LogFeedRowViewModel("entry", "now", "now", "Unmatched", null, "Event", null, null)));
        Assert.Equal(new IntelligenceObjectReference("port", 42, 12), IntelligenceObjectTargets.From(new GraphSource("Ethernet1", new Port { PortId = 42, DeviceId = 12 }))!.Identity);
    }

    [Theory]
    [InlineData("sensor", 73, 12)]
    [InlineData("group", 3, null)]
    [InlineData("location", 4, null)]
    [InlineData("fault", 91, 12)]
    public void Reply_actions_require_verified_identity_and_survive_saved_and_later_replies(string kind, int id, int? device)
    {
        var identity = new IntelligenceObjectReference(kind, id, device, kind == "fault" ? 8 : null);
        var message = IntelligenceMessageViewModel.Assistant();
        message.AddStatusCard(new("Lookup", "Network", DateTimeOffset.UtcNow, new[] {
            new IntelligenceStatusRow("Verified name", "Observed", "Unknown", "unknown", DeviceId: kind == "fault" ? device : null,
                AlertId: kind == "fault" ? id : null, AlertRule: kind == "fault" ? "Rule" : null) { Object = identity }
        }));
        var reference = message.InlineReferences.Single(item => item.Object == identity);
        message.Text = "Check [[" + reference.Key + "]]";
        Assert.Equal(identity, message.SpotlightTarget(reference)!.Identity);
        Assert.Equal(reference.Name, message.SpotlightTarget(reference with { Name = "Invented" })!.Name);
        Assert.Null(message.SpotlightTarget(reference with { Object = identity with { Id = 999 } }));
        var restored = IntelligenceMessageViewModel.Restore(message.Save());
        Assert.Equal(identity, restored.SpotlightTarget(reference)!.Identity);
        var later = IntelligenceMessageViewModel.Assistant();
        later.SeedInlineReferences(restored.InlineReferences);
        later.Text = message.Text;
        Assert.Equal(identity, IntelligenceMessageViewModel.Restore(later.Save()).SpotlightTarget(reference)!.Identity);
    }

    [Fact]
    public void Arbitrary_reply_text_and_status_badges_cannot_create_spotlight_objects()
    {
        var message = IntelligenceMessageViewModel.Assistant();
        message.Text = "[[sensor:999]] [Switch](https://example.net/device/12)";
        Assert.Null(message.SpotlightTarget(new("sensor:999", "Invented", "Up", "up", default, "") { Object = new("sensor", 999, 12) }));
        message.SeedInlineReferences(new[] { new IntelligenceInlineReference("status:up", "Up", "", "up", default, "") });
        Assert.Null(message.SpotlightTarget(Assert.Single(message.InlineReferences)));
    }

    [Fact]
    public void Targets_keep_specific_sensor_and_fault_identity_and_reject_unknown_objects()
    {
        var sensor = IntelligenceObjectTargets.From(new Sensor { SensorId = 73, DeviceId = 12, Description = "Chassis" })!;
        Assert.Equal(new IntelligenceObjectReference("sensor", 73, 12), sensor.Identity);
        var fault = IntelligenceObjectTargets.From(new Alert { Id = 73, DeviceId = 12, RuleId = 4, Source = AlertSource.Fault })!;
        Assert.Equal(new IntelligenceObjectReference("fault", 73, 12, 4), fault.Identity);
        Assert.NotEqual(sensor.Kind, fault.Kind);
        Assert.Null(IntelligenceObjectTargets.From(new object()));
        Assert.Null(IntelligenceObjectTargets.From(new Sensor { SensorId = 73 }));
        var request = new IntelligenceChatRequest(sensor, "https://nms.example.net", "account-a\nhttps://nms.example.net");
        Assert.True(request.Matches(request.Server, request.ChatScope));
        Assert.False(request.Matches("https://other.example.net", request.ChatScope));
        Assert.False(request.Matches(request.Server, "account-b\nhttps://nms.example.net"));
        Assert.False((request with { ChatScope = null }).Matches(request.Server, null));
    }

    [Fact]
    public async Task Scope_rechecks_parent_device_and_alert_source_before_accepting_an_object()
    {
        var sensors = Proxy<ISensorsApi>((_, _) => Task.FromResult<IReadOnlyList<Sensor>>(new[] { new Sensor { SensorId = 73, DeviceId = 12 } }));
        var alerts = Proxy<IAlertsApi>((_, _) => Task.FromResult<Alert?>(new Alert { Id = 73, DeviceId = 12, RuleId = 4, Source = AlertSource.Fault }));
        var client = Proxy<ILibreNmsClient>((method, _) => method.Name == "get_Sensors" ? sensors : alerts);
        var context = Context(client);
        Assert.Null(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("sensor", 73, 99), "Wrong parent")!, default));
        Assert.NotNull(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("sensor", 73, 12), "Sensor")!, default));
        Assert.Null(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("alert", 73, 12, 4), "Legacy alert")!, default));
        Assert.NotNull(await context.ResolveObjectAsync(IntelligenceObjectTargets.Create(new("fault", 73, 12, 4), "Fault")!, default));
    }

    [Fact]
    public void Mixed_spotlight_deduplicates_by_identity_and_locks_during_an_answer()
    {
        var context = Context(Proxy<ILibreNmsClient>((_, _) => null));
        var sensor = IntelligenceObjectTargets.Create(new("sensor", 73, 12), "Temperature")!;
        context.AddSpotlight(new(SpotlightKind.Device, 12, "Switch"));
        context.AddSpotlight(new(SpotlightKind.Group, 3, "Core"));
        context.AddSpotlight(sensor);
        context.AddSpotlight(sensor with { Name = "Renamed" });
        Assert.Equal(3, context.Spotlight.Count);
        Assert.Contains("sensor_id=73, device_id=12", context.SpotlightForAgent);
        context.IsScopeLocked = true;
        Assert.False(context.AddSpotlight(new(SpotlightKind.Location, 4, "Site"), replace: true));
        Assert.Equal(3, context.Spotlight.Count);
        context.IsScopeLocked = false;
        Assert.True(context.AddSpotlight(sensor, replace: true));
        Assert.Equal(sensor, Assert.Single(context.Spotlight));
        context.RestoreChatContext(new[] { new SavedChatSpotlight((int)sensor.Kind, sensor.Id, sensor.Name) { Object = sensor.Object } }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(sensor.Identity, Assert.Single(context.Spotlight).Identity);
    }

    [Fact]
    public void Object_chats_create_a_fresh_tab_and_preserve_existing_drafts()
    {
        var host = new Host();
        using var chats = new IntelligenceChatListViewModel(host, Proxy<IIntelligenceChatStore>((m, _) => m.Name == "Load" ? new SavedChatCollection(null, Array.Empty<SavedIntelligenceChat>()) : null),
            Proxy<IWindowService>((_, _) => null), UiThread.Dispatcher, NullLogger.Instance);
        chats.EnsureScope();
        var first = chats.SelectedChat;
        host.Draft = "Keep this unfinished question";
        chats.NewChat(force: true);
        Assert.Equal("Keep this unfinished question", first!.Data.Draft);
        Assert.NotSame(first, chats.SelectedChat);
        var blank = chats.SelectedChat;
        chats.NewChat(force: true);
        Assert.NotSame(blank, chats.SelectedChat);
        Assert.Equal(3, chats.Chats.Count);
        host.IsBusy = true;
        chats.NewChat(force: true);
        Assert.Equal(3, chats.Chats.Count);
    }

    private static IntelligenceContextViewModel Context(ILibreNmsClient client)
    {
        var settings = Proxy<ISettingsStore>((_, _) => new AppSettings());
        var devices = Proxy<IDeviceCache>((_, _) => null);
        var alerts = new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance);
        return new(devices, client, settings, Proxy<IWindowService>((_, _) => null), alerts, NullLogger.Instance);
    }

    private sealed class Host : IIntelligenceChatHost
    {
        public bool IsBusy { get; set; }
        public bool ShowConversation => true;
        public bool SingleChatMode => false;
        public string ChatScope => "account\nhttps://nms.example.net";
        public string? ErrorText { get; set; }
        public string Draft = "";
        public void CancelAnswer() { }
        public void Capture(SavedIntelligenceChat chat) => chat.Draft = Draft;
        public void Restore(SavedIntelligenceChat? chat) => Draft = chat?.Draft ?? "";
        public SavedIntelligenceChat NewChat() => new();
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var proxy = DispatchProxy.Create<T, Stub>(); ((Stub)(object)proxy).Handler = handler; return proxy; }
}
