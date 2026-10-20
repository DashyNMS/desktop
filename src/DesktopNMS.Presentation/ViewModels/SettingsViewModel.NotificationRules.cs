using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>A choice in Settings › Notifications › Which alerts (#268).</summary>
public sealed record RuleModeOption(NotificationRuleMode Value, string Label, string Description)
{
    public override string ToString() => Label;
}

/// <summary>A choice in Settings › Alert display › what the tray and badge count (#269).</summary>
public sealed record CountFromOption(AlertSeverity Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A rule in Settings' "Which alerts" picker.</summary>
public sealed record RulePickerOption(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One entry in the "Which alerts" list, with its Remove.</summary>
public sealed class NotificationRuleRow
{
    public NotificationRuleRow(NotificationRuleEntry entry, Action<NotificationRuleEntry> remove)
    {
        Entry = entry;
        RemoveCommand = new RelayCommand(() => remove(entry));
    }

    public NotificationRuleEntry Entry { get; }

    public string Description => Entry.Description;

    public RelayCommand RemoveCommand { get; }
}

/// <summary>Settings › Notifications' "Which alerts" (#268) and Alert display's count choice (#269).</summary>
public sealed partial class SettingsViewModel
{
    private RulePickerOption? _ruleToAdd;
    private bool _rulesLoaded;
    private RelayCommand? _addNotificationRuleCommand;

    public IReadOnlyList<RuleModeOption> RuleModeOptions { get; } = new[]
    {
        new RuleModeOption(NotificationRuleMode.All, "Every alert", "Every alert rule can notify, as the severity choices below allow."),
        new RuleModeOption(NotificationRuleMode.AllExcept, "Every alert except…", "Every alert rule can notify except the ones listed here."),
        new RuleModeOption(NotificationRuleMode.Only, "Only…", "Only the alert rules listed here can notify."),
    };

    public RuleModeOption SelectedRuleMode
    {
        get => RuleModeOptions.First(o => o.Value == _draft.Notifications.RuleMode);
        set
        {
            if (value is null || value.Value == _draft.Notifications.RuleMode)
            {
                return;
            }

            _draft.Notifications.RuleMode = value.Value;
            OnPropertyChanged();
            RefreshNotificationRuleRows();
        }
    }

    /// <summary>The list for the chosen mode - each mode keeps its own, so switching loses nothing.</summary>
    public ObservableCollection<NotificationRuleRow> NotificationRuleRows { get; } = new();

    /// <summary>"Every alert" has no list.</summary>
    public bool ShowsNotificationRuleList => _draft.Notifications.RuleMode != NotificationRuleMode.All;

    public bool HasNoNotificationRules => NotificationRuleRows.Count == 0;

    /// <summary>"Only…" with nothing listed notifies about nothing - worth saying.</summary>
    public bool ShowsNothingWillNotify => _draft.Notifications.RuleMode == NotificationRuleMode.Only && NotificationRuleRows.Count == 0;

    public string NotificationRuleListHeading => _draft.Notifications.RuleMode == NotificationRuleMode.Only
        ? "Notify about these"
        : "Leave these out";

    /// <summary>The server's alert rules, for adding one on every device. Loaded when the section opens.</summary>
    public ObservableCollection<RulePickerOption> RulePickerOptions { get; } = new();

    public RulePickerOption? RuleToAdd
    {
        get => _ruleToAdd;
        set
        {
            if (SetProperty(ref _ruleToAdd, value))
            {
                AddNotificationRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddNotificationRuleCommand => _addNotificationRuleCommand ??= new RelayCommand(AddNotificationRule, () => RuleToAdd is not null);

    public IReadOnlyList<CountFromOption> CountFromOptions { get; } =
        AlertCounting.Choices.Select(s => new CountFromOption(s, AlertCounting.Describe(s))).ToList();

    public CountFromOption SelectedCountFrom
    {
        get => CountFromOptions.First(o => o.Value == _draft.Notifications.CountFrom);
        set
        {
            if (value is null || value.Value == _draft.Notifications.CountFrom)
            {
                return;
            }

            _draft.Notifications.CountFrom = value.Value;
            OnPropertyChanged();
        }
    }

    private void AddNotificationRule()
    {
        if (RuleToAdd is not { } rule || _draft.Notifications.RuleMode == NotificationRuleMode.All)
        {
            return;
        }

        NotificationRuleList.Add(_draft.Notifications.RulesFor(_draft.Notifications.RuleMode), rule.Id, rule.Name);
        RuleToAdd = null;
        RefreshNotificationRuleRows();
    }

    private void RemoveNotificationRule(NotificationRuleEntry entry)
    {
        _draft.Notifications.RulesFor(_draft.Notifications.RuleMode).Remove(entry);
        RefreshNotificationRuleRows();
    }

    private void RefreshNotificationRuleRows()
    {
        NotificationRuleRows.Clear();
        foreach (var entry in _draft.Notifications.RulesFor(_draft.Notifications.RuleMode))
        {
            NotificationRuleRows.Add(new NotificationRuleRow(entry, RemoveNotificationRule));
        }

        OnPropertyChanged(nameof(ShowsNotificationRuleList));
        OnPropertyChanged(nameof(HasNoNotificationRules));
        OnPropertyChanged(nameof(ShowsNothingWillNotify));
        OnPropertyChanged(nameof(NotificationRuleListHeading));
    }

    private async Task LoadNotificationRuleChoicesAsync()
    {
        RefreshNotificationRuleRows();
        if (_rulesLoaded || !_session.IsConnected)
        {
            return;
        }

        _rulesLoaded = true;
        try
        {
            var rules = await _client.Rules.ListAsync().ConfigureAwait(true);
            RulePickerOptions.Clear();
            foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                RulePickerOptions.Add(new RulePickerOption(rule.Id, rule.Name ?? $"Rule {rule.Id}"));
            }
        }
        catch (Exception)
        {
            // The picker just stays empty; the right-click menus on the Alerts
            // and Rules pages still add rules.
            _rulesLoaded = false;
        }
    }
}
