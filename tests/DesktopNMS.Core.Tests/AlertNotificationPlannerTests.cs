using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class AlertNotificationPlannerTests
{
    private static readonly DateTime Midday = new(2026, 10, 1, 12, 0, 0);

    [Fact]
    public void A_new_critical_alert_becomes_one_notification_with_its_device_and_rule()
    {
        var change = Change(1, "critical", AlertChangeKind.New, rule: "Device Down");

        var plan = Plan(new[] { change });

        var notification = Assert.Single(plan.Notifications);
        Assert.Equal("Critical: device-1", notification.Title);
        Assert.Equal("Device Down", notification.Body);
        Assert.True(notification.IsProblem);
        Assert.Same(change, notification.Change);
        Assert.False(plan.IsSummary);
    }

    [Theory]
    [InlineData(AlertChangeKind.Reopened, "Critical again: device-1")]
    [InlineData(AlertChangeKind.Recovered, "Recovered: device-1")]
    [InlineData(AlertChangeKind.Unacknowledged, "Unacknowledged: device-1")]
    public void Titles_say_what_happened(AlertChangeKind kind, string title)
    {
        Assert.Equal(title, AlertNotificationPlanner.BuildTitle(Change(1, "critical", kind), "device-1"));
    }

    [Fact]
    public void Recoveries_are_never_sticky_or_noisy()
    {
        var settings = new NotificationSettings();
        settings.Critical.Persistence = ToastPersistence.UntilDismissedWithAlarm;
        settings.Critical.PlaySound = true;

        var plan = Plan(new[] { Change(1, "critical", AlertChangeKind.Recovered) }, settings: settings);

        var notification = Assert.Single(plan.Notifications);
        Assert.Equal(ToastPersistence.Transient, notification.Persistence);
        Assert.False(notification.PlaySound);
    }

    [Fact]
    public void The_first_poll_after_starting_is_quiet_by_default()
    {
        var plan = Plan(new[] { Change(1, "critical", AlertChangeKind.New) }, isFirstPoll: true);

        Assert.Empty(plan.Notifications);
        Assert.Contains("first poll", plan.SuppressionReason);
    }

    [Fact]
    public void Own_acknowledgements_never_notify()
    {
        var settings = new NotificationSettings { NotifyOnAcknowledge = true };

        var plan = Plan(new[] { Change(1, "critical", AlertChangeKind.Acknowledged) }, settings: settings, selfInitiated: _ => true);

        Assert.Empty(plan.Notifications);
    }

    [Fact]
    public void A_switched_off_severity_explains_why_nothing_showed()
    {
        var settings = new NotificationSettings();
        settings.Warning.Enabled = false;

        var plan = Plan(new[] { Change(1, "warning", AlertChangeKind.New) }, settings: settings);

        Assert.Empty(plan.Notifications);
        Assert.Contains("1 warning, which is switched off", plan.SuppressionReason);
    }

    [Fact]
    public void More_changes_than_the_cap_become_one_summary()
    {
        var settings = new NotificationSettings { MaxToastsPerPoll = 2 };
        var changes = Enumerable.Range(1, 3).Select(i => Change(i, "critical", AlertChangeKind.New)).ToList();

        var plan = Plan(changes, settings: settings, location: _ => "Rack 3");

        Assert.True(plan.IsSummary);
        Assert.Equal(3, plan.NotifiableCount);
        var summary = Assert.Single(plan.Notifications);
        Assert.Null(summary.Change);
        Assert.Equal("3 new critical alerts", summary.Title);
        Assert.Equal(AlertSeverity.Critical, summary.Severity);
        Assert.Equal(ToastPersistence.UntilDismissed, summary.Persistence);
    }

    [Fact]
    public void A_note_contributes_only_its_first_line()
    {
        var change = Change(1, "critical", AlertChangeKind.New);
        change.Alert.Note = "Called the vendor\nTicket 1234";

        var notification = Assert.Single(Plan(new[] { change }).Notifications);

        Assert.Equal("Called the vendor", notification.Detail);
    }

    private static AlertNotificationPlan Plan(
        IReadOnlyList<AlertChange> changes,
        bool isFirstPoll = false,
        NotificationSettings? settings = null,
        Func<AlertChange, bool>? selfInitiated = null,
        Func<Alert, string?>? location = null)
        => AlertNotificationPlanner.Plan(
            changes,
            isFirstPoll,
            settings ?? new NotificationSettings(),
            Midday,
            alert => "device-" + alert.DeviceId,
            location,
            selfInitiated);

    private static AlertChange Change(int id, string severity, AlertChangeKind kind, string rule = "Rule")
        => new(new Alert { Id = id, DeviceId = id, SeverityText = severity, RuleName = rule }, kind, previousState: null);
}
