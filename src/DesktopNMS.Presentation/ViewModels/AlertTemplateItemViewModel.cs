using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Templates tab - see <see cref="TemplatesViewModel"/>.</summary>
public sealed class AlertTemplateItemViewModel : ObservableObject
{
    /// <param name="attachedRules">The rules this template drives - resolved by <see cref="TemplatesViewModel"/>, since for the default template LibreNMS derives them rather than storing them.</param>
    public AlertTemplateItemViewModel(AlertTemplate template, IReadOnlyList<string> attachedRules, Action<AlertTemplateItemViewModel> onEdit)
    {
        Template = template;
        EditCommand = new RelayCommand(() => onEdit(this));

        RuleCount = attachedRules.Count;
        // One per line, as the web UI lists them.
        AttachedRuleNames = string.Join(Environment.NewLine, attachedRules);
    }

    public AlertTemplate Template { get; }

    public int Id => Template.Id;

    public string Name => Template.Name ?? $"Template {Template.Id}";

    public string? Title => Template.Title;

    public int RuleCount { get; }

    /// <summary>The attached rules' names, one per line; empty when the template drives no rules.</summary>
    public string AttachedRuleNames { get; }

    public RelayCommand EditCommand { get; }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Template.Template?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
        || AttachedRuleNames.Contains(term, StringComparison.OrdinalIgnoreCase);
}
