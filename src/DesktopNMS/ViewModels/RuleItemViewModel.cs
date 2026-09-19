using System;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Rules tab - see <see cref="RulesViewModel"/>.</summary>
public sealed class RuleItemViewModel : ObservableObject
{
    public RuleItemViewModel(AlertRule rule, Action<RuleItemViewModel> onEdit, Action<RuleItemViewModel> onDelete)
    {
        Rule = rule;
        EditCommand = new RelayCommand(() => onEdit(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public AlertRule Rule { get; }

    public int Id => Rule.Id;

    public string Name => Rule.Name ?? $"Rule {Rule.Id}";

    public AlertSeverity Severity => Rule.Severity;

    public string SeverityText => Severity.ToDisplayString();

    public bool IsDisabled => Rule.Disabled;

    public string? Notes => Rule.Notes;

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

            if (Rule.Devices.Count > 0)
            {
                parts.Add(Rule.Devices.Count == 1 ? "1 device" : $"{Rule.Devices.Count} devices");
            }

            if (Rule.Groups.Count > 0)
            {
                parts.Add(Rule.Groups.Count == 1 ? "1 group" : $"{Rule.Groups.Count} groups");
            }

            if (Rule.Locations.Count > 0)
            {
                parts.Add(Rule.Locations.Count == 1 ? "1 location" : $"{Rule.Locations.Count} locations");
            }

            if (parts.Count == 0)
            {
                return "All devices";
            }

            // invert_map: the list is an exclusion, not a target.
            return (Rule.InvertMap ? "All except " : string.Empty) + string.Join(", ", parts);
        }
    }

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || SeverityText.Contains(term, StringComparison.OrdinalIgnoreCase)
        || TargetSummary.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
}
