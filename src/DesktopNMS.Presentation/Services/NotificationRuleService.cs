using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Services;

/// <summary>The right-click menu's one notifications choice (#268).</summary>
public sealed record NotificationMenuItem(string Header, RelayCommand Command);

/// <summary>
/// The Alerts and Rules pages' right-click notifications choice (#268): one
/// plain item - Core's <see cref="NotificationRuleChoices.Primary"/> picks it,
/// never one that changes which alerts notify at all; that, and everything
/// else, is in Settings › Notifications.
/// </summary>
public sealed class NotificationRuleService
{
    private readonly ISettingsStore _settings;

    public NotificationRuleService(ISettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>The choice for a rule - on an alert's device, or (no device) the rule itself.</summary>
    public NotificationMenuItem ChoiceFor(int ruleId, string ruleName, int? deviceId = null, string? deviceName = null)
    {
        var action = NotificationRuleChoices.Primary(_settings.Current.Notifications, ruleId, deviceId);
        return new NotificationMenuItem(
            NotificationRuleChoices.Describe(action, forRule: deviceId is null),
            new RelayCommand(() =>
            {
                NotificationRuleChoices.Apply(_settings.Current.Notifications, action, ruleId, ruleName, deviceId, deviceName);
                _settings.Save();
            }));
    }
}
