using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>Which alerts send a notification (#268).</summary>
public enum NotificationRuleMode
{
    /// <summary>Every alert, as it always has.</summary>
    All,

    /// <summary>Every alert except the rules in <see cref="Configuration.NotificationSettings.ExceptRules"/>.</summary>
    AllExcept,

    /// <summary>Only the rules in <see cref="Configuration.NotificationSettings.OnlyRules"/>.</summary>
    Only,
}

/// <summary>
/// An alert rule chosen in <see cref="Configuration.NotificationSettings"/> -
/// on every device, or on one device (#268). The names are kept as they were
/// when chosen, so Settings can list them without asking LibreNMS. The same
/// shape as DashyNMS Mobile's <c>IgnoredAlert</c>, so its list carries over.
/// </summary>
public sealed record NotificationRuleEntry(int RuleId, string RuleName, int? DeviceId = null, string? DeviceName = null)
{
    /// <summary>"Port down, on every device", or "Port down on core-sw".</summary>
    [JsonIgnore]
    public string Description => DeviceId is null
        ? $"{RuleName}, on every device"
        : $"{RuleName} on {DeviceName ?? $"device {DeviceId}"}";

    /// <summary>The alert is from this rule, and on this entry's device if it has one.</summary>
    public bool Covers(Alert alert) => alert.RuleId == RuleId && (DeviceId is null || alert.DeviceId == DeviceId);
}

/// <summary>
/// Adding and removing <see cref="NotificationRuleEntry"/>s the way DashyNMS
/// Mobile's ignored list always has: a rule on every device takes in that
/// rule's single-device entries, and the list stays sorted by rule name with
/// a rule's every-device entry before its devices.
/// </summary>
public static class NotificationRuleList
{
    /// <summary>Adds the rule - on one device, or (no device) on all of them. Nothing changes if an entry already covers it.</summary>
    /// <returns>True if the list changed.</returns>
    public static bool Add(List<NotificationRuleEntry> list, int ruleId, string ruleName, int? deviceId = null, string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (list.Any(e => e.RuleId == ruleId && (e.DeviceId is null || e.DeviceId == deviceId)))
        {
            return false;
        }

        if (deviceId is null)
        {
            list.RemoveAll(e => e.RuleId == ruleId);
        }

        list.Add(new NotificationRuleEntry(ruleId, ruleName, deviceId, deviceName));
        Sort(list);
        return true;
    }

    /// <summary>Removes whatever covers <paramref name="alert"/>: its rule's every-device entry, or its device's.</summary>
    public static bool RemoveCovering(List<NotificationRuleEntry> list, Alert alert)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(alert);
        return list.RemoveAll(e => e.Covers(alert)) > 0;
    }

    /// <summary>Removes a whole rule, everywhere it was chosen.</summary>
    public static bool RemoveRule(List<NotificationRuleEntry> list, int ruleId)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.RemoveAll(e => e.RuleId == ruleId) > 0;
    }

    /// <summary>The rule is chosen on every device.</summary>
    public static bool HasEveryDevice(IEnumerable<NotificationRuleEntry> list, int ruleId)
        => list.Any(e => e.RuleId == ruleId && e.DeviceId is null);

    private static void Sort(List<NotificationRuleEntry> list)
    {
        var sorted = list
            .OrderBy(e => e.RuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DeviceId is not null)
            .ThenBy(e => e.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Clear();
        list.AddRange(sorted);
    }
}

/// <summary>What an alert's or a rule's menu can do about its notifications (#268).</summary>
public enum NotificationRuleAction
{
    /// <summary>Leave the rule out on this alert's device ("All except…").</summary>
    LeaveOutOnDevice,

    /// <summary>Leave the rule out on every device ("All except…").</summary>
    LeaveOutEverywhere,

    /// <summary>Notify about the rule on this alert's device ("Only…").</summary>
    OnlyOnDevice,

    /// <summary>Notify about the rule on every device ("Only…").</summary>
    OnlyEverywhere,

    /// <summary>Take the rule (on this device) off "All except…"'s list.</summary>
    NotifyAgain,

    /// <summary>Take the rule (on this device) off "Only…"'s list.</summary>
    StopOnly,
}

/// <summary>
/// Which <see cref="NotificationRuleAction"/>s make sense for a rule, on one
/// device (an alert) or on none (the rule itself), and what each does - so
/// both apps offer the same choices and make the same change.
/// </summary>
public static class NotificationRuleChoices
{
    /// <summary>The choices for <paramref name="ruleId"/>, on <paramref name="deviceId"/> or (null) as a rule.</summary>
    public static IReadOnlyList<NotificationRuleAction> For(NotificationSettings settings, int ruleId, int? deviceId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var choices = new List<NotificationRuleAction>();
        if (settings.RuleMode == NotificationRuleMode.Only)
        {
            var (covered, everywhere) = Coverage(settings.OnlyRules, ruleId, deviceId);
            if (!covered && deviceId is not null)
            {
                choices.Add(NotificationRuleAction.OnlyOnDevice);
            }

            if (!everywhere)
            {
                choices.Add(NotificationRuleAction.OnlyEverywhere);
            }

            if (covered)
            {
                choices.Add(NotificationRuleAction.StopOnly);
            }

            return choices;
        }

        var (left, leftEverywhere) = settings.RuleMode == NotificationRuleMode.AllExcept
            ? Coverage(settings.ExceptRules, ruleId, deviceId)
            : (false, false);

        if (!left && deviceId is not null)
        {
            choices.Add(NotificationRuleAction.LeaveOutOnDevice);
        }

        if (!leftEverywhere)
        {
            choices.Add(NotificationRuleAction.LeaveOutEverywhere);
        }

        if (left)
        {
            choices.Add(NotificationRuleAction.NotifyAgain);
        }

        if (deviceId is not null)
        {
            choices.Add(NotificationRuleAction.OnlyOnDevice);
        }

        choices.Add(NotificationRuleAction.OnlyEverywhere);
        return choices;
    }

    /// <summary>
    /// The one choice a right-click menu offers, never one that changes mode -
    /// that's left to Settings: in "Only…", notify about the rule or stop;
    /// otherwise, leave it out or notify again. On an alert it's that alert's
    /// device; on a rule, every device.
    /// </summary>
    public static NotificationRuleAction Primary(NotificationSettings settings, int ruleId, int? deviceId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.RuleMode == NotificationRuleMode.Only)
        {
            return Coverage(settings.OnlyRules, ruleId, deviceId).Covered
                ? NotificationRuleAction.StopOnly
                : deviceId is null ? NotificationRuleAction.OnlyEverywhere : NotificationRuleAction.OnlyOnDevice;
        }

        var left = settings.RuleMode == NotificationRuleMode.AllExcept && Coverage(settings.ExceptRules, ruleId, deviceId).Covered;
        return left
            ? NotificationRuleAction.NotifyAgain
            : deviceId is null ? NotificationRuleAction.LeaveOutEverywhere : NotificationRuleAction.LeaveOutOnDevice;
    }

    /// <summary>The choice moves notifications into another mode - "Only…" from everything else - which is worth confirming first.</summary>
    public static bool ChangesMode(NotificationSettings settings, NotificationRuleAction action)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return action switch
        {
            NotificationRuleAction.OnlyOnDevice or NotificationRuleAction.OnlyEverywhere => settings.RuleMode != NotificationRuleMode.Only,
            NotificationRuleAction.LeaveOutOnDevice or NotificationRuleAction.LeaveOutEverywhere => settings.RuleMode == NotificationRuleMode.Only,
            _ => false,
        };
    }

    /// <summary>Makes the change. A "leave out" choice moves to "All except…", an "only" choice to "Only…".</summary>
    public static void Apply(NotificationSettings settings, NotificationRuleAction action, int ruleId, string ruleName, int? deviceId = null, string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (action)
        {
            case NotificationRuleAction.LeaveOutOnDevice when deviceId is not null:
                settings.RuleMode = NotificationRuleMode.AllExcept;
                NotificationRuleList.Add(settings.ExceptRules, ruleId, ruleName, deviceId, deviceName);
                break;
            case NotificationRuleAction.LeaveOutEverywhere:
                settings.RuleMode = NotificationRuleMode.AllExcept;
                NotificationRuleList.Add(settings.ExceptRules, ruleId, ruleName);
                break;
            case NotificationRuleAction.OnlyOnDevice when deviceId is not null:
                settings.RuleMode = NotificationRuleMode.Only;
                NotificationRuleList.Add(settings.OnlyRules, ruleId, ruleName, deviceId, deviceName);
                break;
            case NotificationRuleAction.OnlyEverywhere:
                settings.RuleMode = NotificationRuleMode.Only;
                NotificationRuleList.Add(settings.OnlyRules, ruleId, ruleName);
                break;
            case NotificationRuleAction.NotifyAgain:
                Remove(settings.ExceptRules, ruleId, deviceId);
                break;
            case NotificationRuleAction.StopOnly:
                Remove(settings.OnlyRules, ruleId, deviceId);
                break;
        }
    }

    /// <summary>The menu's words: "Don't notify me about this rule on this device", and so on.</summary>
    public static string Describe(NotificationRuleAction action, bool forRule = false) => action switch
    {
        NotificationRuleAction.LeaveOutOnDevice => "Don't notify me about this rule on this device",
        NotificationRuleAction.LeaveOutEverywhere => forRule ? "Don't notify me about this rule" : "Don't notify me about this rule on any device",
        NotificationRuleAction.OnlyOnDevice => "Notify me about this rule on this device",
        NotificationRuleAction.OnlyEverywhere => forRule ? "Notify me about this rule" : "Notify me about this rule on every device",
        NotificationRuleAction.NotifyAgain => "Notify me about this again",
        NotificationRuleAction.StopOnly => "Stop notifying me about this",
        _ => action.ToString(),
    };

    /// <summary>Whether the list covers the rule here, and whether it covers it on every device.</summary>
    private static (bool Covered, bool Everywhere) Coverage(List<NotificationRuleEntry> list, int ruleId, int? deviceId)
    {
        var everywhere = NotificationRuleList.HasEveryDevice(list, ruleId);
        var covered = everywhere || (deviceId is null
            ? list.Any(e => e.RuleId == ruleId)
            : list.Any(e => e.RuleId == ruleId && e.DeviceId == deviceId));
        return (covered, everywhere);
    }

    /// <summary>On a device, whatever covers it there; as a rule, the rule everywhere.</summary>
    private static void Remove(List<NotificationRuleEntry> list, int ruleId, int? deviceId)
    {
        if (deviceId is null)
        {
            NotificationRuleList.RemoveRule(list, ruleId);
        }
        else
        {
            list.RemoveAll(e => e.RuleId == ruleId && (e.DeviceId is null || e.DeviceId == deviceId));
        }
    }
}

/// <summary>
/// Reads a list of <see cref="NotificationRuleEntry"/> that can't be read -
/// a hand edit, or a shape from a newer version - as empty rather than
/// failing the whole settings file, so it never stops the alert check (#268).
/// </summary>
public sealed class TolerantRuleListConverter : JsonConverter<List<NotificationRuleEntry>>
{
    public override List<NotificationRuleEntry> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<NotificationRuleEntry>();
        }

        var list = new List<NotificationRuleEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            try
            {
                if (element.Deserialize<NotificationRuleEntry>(options) is { } entry)
                {
                    list.Add(entry);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                // One unreadable entry is left out; the rest still count.
            }
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<NotificationRuleEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
        {
            JsonSerializer.Serialize(writer, entry, options);
        }

        writer.WriteEndArray();
    }
}
