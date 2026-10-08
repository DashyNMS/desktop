using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Services;

/// <summary>One choice on an alert's or a rule's Notifications menu (#268).</summary>
public sealed record NotificationMenuItem(string Header, RelayCommand Command);

/// <summary>
/// The Alerts and Rules pages' Notifications menu (#268): Core's
/// <see cref="NotificationRuleChoices"/> decides what's offered and what
/// each choice does; this asks before a choice changes which alerts notify
/// at all, and saves it.
/// </summary>
public sealed class NotificationRuleService
{
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;

    public NotificationRuleService(ISettingsStore settings, IWindowService windows)
    {
        _settings = settings;
        _windows = windows;
    }

    /// <summary>The menu for a rule - on an alert's device, or (no device) the rule itself.</summary>
    public IReadOnlyList<NotificationMenuItem> MenuFor(int ruleId, string ruleName, int? deviceId = null, string? deviceName = null)
        => NotificationRuleChoices.For(_settings.Current.Notifications, ruleId, deviceId)
            .Select(action => new NotificationMenuItem(
                NotificationRuleChoices.Describe(action, forRule: deviceId is null),
                new RelayCommand(() => Choose(action, ruleId, ruleName, deviceId, deviceName))))
            .ToList();

    private void Choose(NotificationRuleAction action, int ruleId, string ruleName, int? deviceId, string? deviceName)
    {
        var notifications = _settings.Current.Notifications;
        if (NotificationRuleChoices.ChangesMode(notifications, action)
            && !_windows.Confirm(
                "Only notify about chosen rules",
                $"From now on only {ruleName} and any other rules you choose send notifications. Every other alert stays quiet, but still lists on the Alerts page and still counts on the tray icon. You can change this in Settings › Notifications.",
                "Only notify about these"))
        {
            return;
        }

        NotificationRuleChoices.Apply(notifications, action, ruleId, ruleName, deviceId, deviceName);
        _settings.Save();
    }
}
