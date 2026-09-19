using System;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Rules tab - see <see cref="RulesViewModel"/>.</summary>
public sealed class RuleItemViewModel : ObservableObject
{
    private AlertRule _rule;
    private int _activeAlertCount;
    private bool _isToggling;

    public RuleItemViewModel(
        AlertRule rule,
        Action<RuleItemViewModel> onEdit,
        Action<RuleItemViewModel> onDelete,
        Action<RuleItemViewModel> onToggleDisabled,
        Action<RuleItemViewModel> onShowAlerts)
    {
        _rule = rule;
        EditCommand = new RelayCommand(() => onEdit(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
        ToggleDisabledCommand = new RelayCommand(() => onToggleDisabled(this), () => !_isToggling);
        ShowAlertsCommand = new RelayCommand(() => onShowAlerts(this), () => HasActiveAlerts);
    }

    public AlertRule Rule => _rule;

    public int Id => _rule.Id;

    public string Name => _rule.Name ?? $"Rule {_rule.Id}";

    public AlertSeverity Severity => _rule.Severity;

    public string SeverityText => Severity.ToDisplayString();

    /// <summary>Sort key for the Severity column - most severe first, rather than the alphabetical order the display text would give.</summary>
    public int SeverityRank => Severity switch
    {
        AlertSeverity.Critical => 0,
        AlertSeverity.Warning => 1,
        AlertSeverity.Ok => 2,
        _ => 3,
    };

    /// <summary>
    /// The condition as LibreNMS's own rule list shows it - the builder
    /// rendered by <see cref="AlertRuleSqlFormatter"/>, or the hand-written
    /// SQL when the rule overrides its query (that being what actually runs).
    /// </summary>
    public string ConditionText => _rule.Extra?.OverrideQuery == true && !string.IsNullOrWhiteSpace(_rule.Query)
        ? _rule.Query
        : AlertRuleSqlFormatter.Format(_rule.Builder) ?? _rule.Rule ?? string.Empty;

    public string ConditionToolTip => _rule.Extra?.OverrideQuery == true ? "Override SQL - runs exactly as written" : ConditionText;

    public bool IsDisabled => _rule.Disabled;

    public string StatusToolTip => IsDisabled ? "Disabled - click to enable" : "Enabled - click to disable";

    public string? Notes => _rule.Notes;

    /// <summary>
    /// "3 devices, 2 groups", "1 location", or "All devices" when every
    /// targeting list is empty - LibreNMS treats an untargeted rule as
    /// applying fleet-wide.
    /// </summary>
    public string TargetSummary
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();

            if (_rule.Devices.Count > 0)
            {
                parts.Add(_rule.Devices.Count == 1 ? "1 device" : $"{_rule.Devices.Count} devices");
            }

            if (_rule.Groups.Count > 0)
            {
                parts.Add(_rule.Groups.Count == 1 ? "1 group" : $"{_rule.Groups.Count} groups");
            }

            if (_rule.Locations.Count > 0)
            {
                parts.Add(_rule.Locations.Count == 1 ? "1 location" : $"{_rule.Locations.Count} locations");
            }

            if (parts.Count == 0)
            {
                return "All devices";
            }

            // invert_map: the list is an exclusion, not a target.
            return (_rule.InvertMap ? "All except " : string.Empty) + string.Join(", ", parts);
        }
    }

    /// <summary>Alerts currently raised by this rule - active or acknowledged, never recovered. Fed by <see cref="RulesViewModel"/> from the alert monitor.</summary>
    public int ActiveAlertCount
    {
        get => _activeAlertCount;
        set
        {
            if (SetProperty(ref _activeAlertCount, value))
            {
                OnPropertyChanged(nameof(HasActiveAlerts));
                OnPropertyChanged(nameof(ActiveAlertsToolTip));
                ShowAlertsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasActiveAlerts => _activeAlertCount > 0;

    public string ActiveAlertsToolTip => _activeAlertCount == 1
        ? "1 alert raised by this rule - click to show it in Alerts"
        : $"{_activeAlertCount} alerts raised by this rule - click to show them in Alerts";

    /// <summary>True while the enable/disable write is in flight, so the toggle can't be double-clicked.</summary>
    public bool IsToggling
    {
        get => _isToggling;
        set
        {
            if (SetProperty(ref _isToggling, value))
            {
                ToggleDisabledCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ToggleDisabledCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    /// <summary>Swaps in a fresh copy of the rule after a write, re-raising every derived property.</summary>
    public void Update(AlertRule rule)
    {
        _rule = rule;
        OnPropertyChanged(string.Empty);
    }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || SeverityText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || TargetSummary.Contains(term, StringComparison.OrdinalIgnoreCase)
        || ConditionText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
}
