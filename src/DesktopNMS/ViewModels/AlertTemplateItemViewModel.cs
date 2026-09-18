using System;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Templates tab - see <see cref="TemplatesViewModel"/>.</summary>
public sealed class AlertTemplateItemViewModel : ObservableObject
{
    public AlertTemplateItemViewModel(AlertTemplate template, Action<AlertTemplateItemViewModel> onEdit)
    {
        Template = template;
        EditCommand = new RelayCommand(() => onEdit(this));
    }

    public AlertTemplate Template { get; }

    public int Id => Template.Id;

    public string Name => Template.Name ?? $"Template {Template.Id}";

    public string? Title => Template.Title;

    public int RuleCount => Template.AlertRules.Count;

    public RelayCommand EditCommand { get; }

    public bool Matches(string term) =>
        Name.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
}
