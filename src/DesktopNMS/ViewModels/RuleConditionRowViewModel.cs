using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One editable leaf condition in the rule builder (issue #22) - a field,
/// operator and value, matching one entry in an <see cref="AlertConditionNode"/>
/// group's <c>Rules</c> list.
/// </summary>
public sealed class RuleConditionRowViewModel : ObservableObject
{
    private static readonly IReadOnlyList<AlertConditionField> AllFields =
        AlertConditionFields.Groups.SelectMany(g => g.Fields).ToList();

    private AlertConditionField _selectedField;
    private AlertConditionOperator? _selectedOperator;
    private string _value;

    public RuleConditionRowViewModel(Action<RuleConditionRowViewModel> onRemove, AlertConditionField? field = null, string? operatorValue = null, string? value = null)
    {
        RemoveCommand = new RelayCommand(() => onRemove(this));

        _selectedField = field ?? AllFields[0];
        _selectedOperator = _selectedField.Operators.FirstOrDefault(o => o.Value == operatorValue) ?? _selectedField.Operators.FirstOrDefault();
        _value = value ?? string.Empty;
    }

    public IReadOnlyList<AlertConditionField> Fields => AllFields;

    public AlertConditionField SelectedField
    {
        get => _selectedField;
        set
        {
            if (SetProperty(ref _selectedField, value))
            {
                OnPropertyChanged(nameof(Operators));
                SelectedOperator = value.Operators.FirstOrDefault();
            }
        }
    }

    public IReadOnlyList<AlertConditionOperator> Operators => SelectedField.Operators;

    public AlertConditionOperator? SelectedOperator
    {
        get => _selectedOperator;
        set => SetProperty(ref _selectedOperator, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public RelayCommand RemoveCommand { get; }

    public AlertConditionNode ToNode() => new()
    {
        Id = SelectedField.Field,
        Field = SelectedField.Field,
        Type = SelectedField.Type,
        Input = SelectedField.Input,
        Operator = SelectedOperator?.Value ?? "equal",
        Value = Value,
        Valid = true,
    };
}
