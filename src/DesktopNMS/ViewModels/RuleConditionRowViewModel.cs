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
    private string _fieldSearchText;

    public RuleConditionRowViewModel(Action<RuleConditionRowViewModel> onRemove, AlertConditionField? field = null, string? operatorValue = null, string? value = null)
    {
        RemoveCommand = new RelayCommand(() => onRemove(this));

        _selectedField = field ?? AlertConditionFields.Resolve("devices.hostname");
        _selectedOperator = _selectedField.Operators.FirstOrDefault(o => o.Value == operatorValue) ?? _selectedField.Operators.FirstOrDefault();
        _value = value ?? string.Empty;
        _fieldSearchText = _selectedField.Field;
    }

    /// <summary>
    /// The field list the picker shows, narrowed by <see cref="FieldSearchText"/>.
    /// The catalog is ~1,385 entries (the same count LibreNMS's own rule
    /// builder offers), so the picker is an editable ComboBox and this is what
    /// typing into it filters.
    /// </summary>
    public IReadOnlyList<AlertConditionField> Fields
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_fieldSearchText))
            {
                return AllFields;
            }

            var matches = AllFields
                .Where(f => f.Field.Contains(_fieldSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Never hand back an empty list: WPF nulls SelectedItem the moment
            // the bound item leaves ItemsSource, which would silently drop
            // this row's field while the user was still mid-type.
            return matches.Count > 0 ? matches : new List<AlertConditionField> { _selectedField };
        }
    }

    /// <summary>What the user has typed into the field picker - not necessarily a real field name.</summary>
    public string FieldSearchText
    {
        get => _fieldSearchText;
        set
        {
            if (SetProperty(ref _fieldSearchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Fields));
            }
        }
    }

    public AlertConditionField SelectedField
    {
        get => _selectedField;
        set
        {
            // WPF pushes null through this binding whenever filtering removes
            // the current item from the list; keeping the last real selection
            // is what makes typing-to-filter non-destructive.
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedField, value))
            {
                // Set the backing field rather than the property so picking an
                // item doesn't immediately re-filter the list out from under
                // the open dropdown.
                _fieldSearchText = value.Field;
                OnPropertyChanged(nameof(FieldSearchText));
                OnPropertyChanged(nameof(Operators));
                OnPropertyChanged(nameof(ValueHint));

                if (SelectedOperator is null || !value.Operators.Any(o => o.Value == SelectedOperator.Value))
                {
                    SelectedOperator = value.Operators.FirstOrDefault();
                }
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

    /// <summary>
    /// The values LibreNMS restricts this field to, for fields backed by an
    /// enum column or a yes/no macro - shown as the value box's tooltip, since
    /// otherwise there's nothing telling you a field only accepts "1"/"0" or
    /// "noAuthNoPriv"/"authNoPriv"/"authPriv".
    /// </summary>
    public string? ValueHint => SelectedField.Values.Count == 0
        ? null
        : "Allowed values: " + string.Join(", ", SelectedField.Values);

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
