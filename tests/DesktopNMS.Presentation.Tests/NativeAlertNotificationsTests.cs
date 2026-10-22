using System.Reflection;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class NativeAlertNotificationsTests
{
    [Fact]
    public async Task Group_faults_share_one_notification_and_updates_keep_their_identity_and_actions()
    {
        var backend = new Backend();
        using var notifications = Create(backend);
        var first = Alert(1);
        var second = Alert(2);
        var change = new AlertChange(first, AlertChangeKind.New, null, new[] { first, second }, 2, 2, 0);
        notifications.Handle(Poll(change));
        await notifications.WhenIdle;
        var sent = Assert.Single(backend.Shown);
        Assert.Equal("fault:1", sent.Alert);
        Assert.True(sent.CanAcknowledge);

        notifications.Handle(Poll(new AlertChange(second, AlertChangeKind.Updated, AlertState.Active, new[] { second }, 1, 0, 1)));
        await notifications.WhenIdle;
        Assert.Equal(2, backend.Shown.Count);
        Assert.Equal(sent.Id, backend.Shown.Last().Id);
        Assert.True(backend.Shown.Last().IsUpdate);
        Assert.True(backend.Shown.Last().CanAcknowledge);
    }

    [Fact]
    public async Task A_quiet_first_poll_sends_nothing_and_baseline_withdraws_old_group_actions()
    {
        var backend = new Backend();
        using var notifications = Create(backend);
        notifications.Handle(Poll(new AlertChange(Alert(1), AlertChangeKind.New, null), first: true));
        await notifications.WhenIdle;
        Assert.Empty(backend.Shown);
        notifications.Handle(Poll(new AlertChange(Alert(1), AlertChangeKind.New, null)));
        notifications.ShowPreview(AlertSeverity.Warning);
        await notifications.WhenIdle;
        notifications.Handle(Poll(baseline: true));
        await notifications.WhenIdle;
        Assert.Equal(backend.Shown.First().Id, Assert.Single(backend.Closed));
        Assert.DoesNotContain("preview", backend.Closed);
    }

    [Fact]
    public async Task Recovery_withdraws_the_old_problem_even_when_recovery_notifications_are_off()
    {
        var backend = new Backend();
        var settings = new AppSettings();
        settings.Notifications.NotifyOnRecovery = false;
        using var notifications = Create(backend, settings);
        notifications.Handle(Poll(new AlertChange(Alert(1), AlertChangeKind.New, null)));
        await notifications.WhenIdle;
        var recovered = Alert(1);
        recovered.StateValue = 0;
        notifications.Handle(Poll(new AlertChange(recovered, AlertChangeKind.Recovered, AlertState.Active)));
        await notifications.WhenIdle;
        Assert.Single(backend.Shown);
        Assert.Equal(backend.Shown[0].Id, Assert.Single(backend.Closed));
    }

    [Fact]
    public async Task Delivery_failures_do_not_break_the_queue_and_actions_keep_typed_fault_ids()
    {
        var backend = new Backend { Fail = true };
        using var notifications = Create(backend);
        notifications.ShowPreview(AlertSeverity.Critical);
        await notifications.WhenIdle;
        backend.Fail = false;
        notifications.ShowPreview(AlertSeverity.Warning);
        await notifications.WhenIdle;
        Assert.Single(backend.Shown);
        ToastActionRequest? received = null;
        notifications.ActionRequested += (_, action) => received = action;
        backend.Activate(new(ToastAction.Acknowledge, Alert(9).Identity));
        Assert.Equal("fault:9", received?.Alert?.ToString());
    }

    private static Alert Alert(int id) => new() { Id = id, DeviceId = 1, RuleId = 2, Source = AlertSource.Fault, StateValue = 1,
        SeverityText = "critical", Hostname = "switch.example.net", RuleName = "Device unavailable" };

    private static AlertPollResult Poll(AlertChange? change = null, bool first = false, bool baseline = false) => new()
    {
        Succeeded = true, CompletedAt = DateTimeOffset.Now, IsFirstPoll = first, IsBaseline = baseline,
        Alerts = change is null ? Array.Empty<Alert>() : new[] { change.Alert },
        Changes = change is null ? Array.Empty<AlertChange>() : new[] { change },
    };

    private static NativeAlertNotifications Create(Backend backend, AppSettings? settings = null)
        => new(Proxy<ISettingsStore>((_, _) => settings ?? new AppSettings()), Proxy<IDeviceCache>((_, _) => null),
            Proxy<ISelfActionTracker>((_, _) => false), Proxy<ILibreNmsClient>((_, _) => new ApiPermissions()),
            backend, NullLogger<NativeAlertNotifications>.Instance);

    private sealed class Backend : INativeNotificationBackend
    {
        public bool Fail;
        public List<NativeNotification> Shown = new();
        public List<string> Closed = new();
        public event EventHandler<ToastActionRequest>? ActionRequested;
        public void Activate(ToastActionRequest action) => ActionRequested?.Invoke(this, action);
        public Task ShowAsync(NativeNotification notification) { if (Fail) throw new IOException("Example failure"); Shown.Add(notification); return Task.CompletedTask; }
        public Task CloseAsync(string id) { Closed.Add(id); return Task.CompletedTask; }
        public void Dispose() { }
    }

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
