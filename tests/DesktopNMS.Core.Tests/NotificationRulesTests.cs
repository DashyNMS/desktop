using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class NotificationRulesTests
{
    private static readonly DateTime Midday = new(2026, 10, 1, 12, 0, 0);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Every_alert_notifies_by_default()
    {
        var settings = new NotificationSettings();

        Assert.Equal(NotificationRuleMode.All, settings.RuleMode);
        Assert.True(settings.AllowsRule(Alert(rule: 5, device: 1)));
    }

    [Fact]
    public void All_except_leaves_out_a_rule_on_every_device()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.AllExcept };
        NotificationRuleList.Add(settings.ExceptRules, 5, "Port down");

        Assert.False(settings.AllowsRule(Alert(rule: 5, device: 1)));
        Assert.False(settings.AllowsRule(Alert(rule: 5, device: 2)));
        Assert.True(settings.AllowsRule(Alert(rule: 6, device: 1)));
    }

    [Fact]
    public void All_except_on_one_device_leaves_the_others_notifying()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.AllExcept };
        NotificationRuleList.Add(settings.ExceptRules, 5, "Port down", 1, "core-sw");

        Assert.False(settings.AllowsRule(Alert(rule: 5, device: 1)));
        Assert.True(settings.AllowsRule(Alert(rule: 5, device: 2)));
    }

    [Fact]
    public void Only_notifies_about_the_listed_rules()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.Only };
        NotificationRuleList.Add(settings.OnlyRules, 5, "Port down");
        NotificationRuleList.Add(settings.OnlyRules, 7, "Device down", 2, "edge-fw");

        Assert.True(settings.AllowsRule(Alert(rule: 5, device: 9)));
        Assert.True(settings.AllowsRule(Alert(rule: 7, device: 2)));
        Assert.False(settings.AllowsRule(Alert(rule: 7, device: 3)));
        Assert.False(settings.AllowsRule(Alert(rule: 8, device: 2)));
    }

    [Fact]
    public void Only_with_an_empty_list_notifies_about_nothing()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.Only };

        Assert.False(settings.AllowsRule(Alert(rule: 5, device: 1)));
    }

    [Fact]
    public void Switching_modes_keeps_both_lists()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.AllExcept };
        NotificationRuleList.Add(settings.ExceptRules, 5, "Port down");
        settings.RuleMode = NotificationRuleMode.Only;
        NotificationRuleList.Add(settings.OnlyRules, 7, "Device down");

        settings.RuleMode = NotificationRuleMode.AllExcept;

        Assert.Single(settings.ExceptRules);
        Assert.Single(settings.OnlyRules);
        Assert.False(settings.AllowsRule(Alert(rule: 5, device: 1)));
        Assert.True(settings.AllowsRule(Alert(rule: 7, device: 1)));
    }

    [Fact]
    public void Every_device_takes_in_the_rules_single_devices()
    {
        var list = new List<NotificationRuleEntry>();
        NotificationRuleList.Add(list, 5, "Port down", 1, "core-sw");
        NotificationRuleList.Add(list, 5, "Port down", 2, "edge-fw");

        Assert.True(NotificationRuleList.Add(list, 5, "Port down"));

        var entry = Assert.Single(list);
        Assert.Null(entry.DeviceId);
        Assert.False(NotificationRuleList.Add(list, 5, "Port down", 3, "dist-sw"));
    }

    [Fact]
    public void The_list_sorts_by_rule_with_every_device_first()
    {
        var list = new List<NotificationRuleEntry>();
        NotificationRuleList.Add(list, 7, "Port down", 2, "edge-fw");
        NotificationRuleList.Add(list, 5, "Device down", 1, "core-sw");
        NotificationRuleList.Add(list, 5, "Device down");

        Assert.Equal(new[] { "Device down, on every device", "Port down on edge-fw" }, list.Select(e => e.Description));
    }

    [Fact]
    public void Notify_again_removes_whatever_covers_the_alert()
    {
        var list = new List<NotificationRuleEntry>();
        NotificationRuleList.Add(list, 5, "Port down", 1, "core-sw");
        NotificationRuleList.Add(list, 6, "Device down");

        Assert.True(NotificationRuleList.RemoveCovering(list, Alert(rule: 5, device: 1)));
        Assert.False(NotificationRuleList.RemoveCovering(list, Alert(rule: 5, device: 1)));
        Assert.True(NotificationRuleList.RemoveRule(list, 6));
        Assert.Empty(list);
    }

    [Fact]
    public void A_left_out_alert_makes_no_notification_and_the_plan_says_why()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.AllExcept };
        NotificationRuleList.Add(settings.ExceptRules, 5, "Port down");

        var plan = AlertNotificationPlanner.Plan(new[] { Change(rule: 5) }, isFirstPoll: false, settings, Midday, a => a.DisplayHostname);

        Assert.Empty(plan.Notifications);
        Assert.Equal(1, plan.LeftOutByRuleCount);
        Assert.Contains("left out by the alert rules", plan.SuppressionReason);
    }

    [Fact]
    public void Rules_are_weighed_before_severity_and_quiet_hours()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.Only, QuietHoursEnabled = true, QuietHoursStartHour = 0, QuietHoursEndHour = 23 };
        NotificationRuleList.Add(settings.OnlyRules, 5, "Port down");

        var plan = AlertNotificationPlanner.Plan(new[] { Change(rule: 5), Change(rule: 6, id: 2) }, isFirstPoll: false, settings, Midday, a => a.DisplayHostname);

        Assert.Single(plan.Notifications);
        Assert.Equal(1, plan.LeftOutByRuleCount);
    }

    [Fact]
    public void The_lists_round_trip_through_the_settings_file()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.Only };
        NotificationRuleList.Add(settings.OnlyRules, 5, "Port down", 1, "core-sw");

        var json = JsonSerializer.Serialize(settings, Options);
        var read = JsonSerializer.Deserialize<NotificationSettings>(json, Options)!;

        Assert.Equal(NotificationRuleMode.Only, read.RuleMode);
        Assert.Equal(settings.OnlyRules, read.OnlyRules);
    }

    [Fact]
    public void An_unreadable_list_counts_as_empty_rather_than_failing()
    {
        const string json = """{ "ruleMode": "Sideways", "exceptRules": { "not": "a list" }, "onlyRules": [ 42, { "ruleId": 5, "ruleName": "Port down" } ] }""";

        var read = JsonSerializer.Deserialize<NotificationSettings>(json, Options)!;
        read.Normalise();

        Assert.Equal(NotificationRuleMode.All, read.RuleMode);
        Assert.Empty(read.ExceptRules);
        Assert.Equal(5, Assert.Single(read.OnlyRules).RuleId);
    }

    [Fact]
    public void Clone_copies_the_mode_and_lists_without_sharing_them()
    {
        var settings = new NotificationSettings { RuleMode = NotificationRuleMode.AllExcept, CountFrom = AlertSeverity.Warning };
        NotificationRuleList.Add(settings.ExceptRules, 5, "Port down");

        var clone = settings.Clone();
        NotificationRuleList.Add(clone.ExceptRules, 6, "Device down");

        Assert.Equal(NotificationRuleMode.AllExcept, clone.RuleMode);
        Assert.Equal(AlertSeverity.Warning, clone.CountFrom);
        Assert.Single(settings.ExceptRules);
        Assert.Equal(2, clone.ExceptRules.Count);
    }

    [Fact]
    public void An_alert_offers_leaving_out_or_only_while_every_alert_notifies()
    {
        var choices = NotificationRuleChoices.For(new NotificationSettings(), 5, deviceId: 1);

        Assert.Equal(
            new[] { NotificationRuleAction.LeaveOutOnDevice, NotificationRuleAction.LeaveOutEverywhere, NotificationRuleAction.OnlyOnDevice, NotificationRuleAction.OnlyEverywhere },
            choices);
    }

    [Fact]
    public void Leaving_out_on_one_device_then_offers_notify_again()
    {
        var settings = new NotificationSettings();
        NotificationRuleChoices.Apply(settings, NotificationRuleAction.LeaveOutOnDevice, 5, "Port down", 1, "core-sw");

        Assert.Equal(NotificationRuleMode.AllExcept, settings.RuleMode);
        var choices = NotificationRuleChoices.For(settings, 5, deviceId: 1);
        Assert.Contains(NotificationRuleAction.NotifyAgain, choices);
        Assert.Contains(NotificationRuleAction.LeaveOutEverywhere, choices);
        Assert.DoesNotContain(NotificationRuleAction.LeaveOutOnDevice, choices);

        NotificationRuleChoices.Apply(settings, NotificationRuleAction.NotifyAgain, 5, "Port down", 1, "core-sw");
        Assert.Empty(settings.ExceptRules);
    }

    [Fact]
    public void Only_is_worth_confirming_from_another_mode_and_then_offers_stop()
    {
        var settings = new NotificationSettings();

        Assert.True(NotificationRuleChoices.ChangesMode(settings, NotificationRuleAction.OnlyEverywhere));
        NotificationRuleChoices.Apply(settings, NotificationRuleAction.OnlyEverywhere, 5, "Port down");

        Assert.Equal(NotificationRuleMode.Only, settings.RuleMode);
        Assert.False(NotificationRuleChoices.ChangesMode(settings, NotificationRuleAction.OnlyOnDevice));
        Assert.Equal(new[] { NotificationRuleAction.StopOnly }, NotificationRuleChoices.For(settings, 5, deviceId: 1));
        Assert.Equal(new[] { NotificationRuleAction.OnlyOnDevice, NotificationRuleAction.OnlyEverywhere }, NotificationRuleChoices.For(settings, 6, deviceId: 1));
    }

    [Fact]
    public void The_right_click_choice_never_changes_mode()
    {
        var settings = new NotificationSettings();
        Assert.Equal(NotificationRuleAction.LeaveOutOnDevice, NotificationRuleChoices.Primary(settings, 5, deviceId: 1));
        Assert.Equal(NotificationRuleAction.LeaveOutEverywhere, NotificationRuleChoices.Primary(settings, 5, deviceId: null));

        NotificationRuleChoices.Apply(settings, NotificationRuleAction.LeaveOutOnDevice, 5, "Port down", 1, "core-sw");
        Assert.Equal(NotificationRuleAction.NotifyAgain, NotificationRuleChoices.Primary(settings, 5, deviceId: 1));

        settings.RuleMode = NotificationRuleMode.Only;
        Assert.Equal(NotificationRuleAction.OnlyOnDevice, NotificationRuleChoices.Primary(settings, 5, deviceId: 1));
        NotificationRuleChoices.Apply(settings, NotificationRuleAction.OnlyEverywhere, 5, "Port down");
        Assert.Equal(NotificationRuleAction.StopOnly, NotificationRuleChoices.Primary(settings, 5, deviceId: 1));
    }

    [Fact]
    public void A_rule_has_no_device_choices()
    {
        var choices = NotificationRuleChoices.For(new NotificationSettings(), 5, deviceId: null);

        Assert.Equal(new[] { NotificationRuleAction.LeaveOutEverywhere, NotificationRuleAction.OnlyEverywhere }, choices);
    }

    private static Alert Alert(int rule, int device, int id = 1, string severity = "critical") => new()
    {
        Id = id,
        RuleId = rule,
        DeviceId = device,
        StateValue = 1,
        SeverityText = severity,
        RuleName = $"Rule {rule}",
    };

    private static AlertChange Change(int rule, int id = 1) => new(Alert(rule, device: 1, id), AlertChangeKind.New, previousState: null);
}
