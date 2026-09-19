using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One editable leaf condition in the rule builder (issue #22) - a field,
/// operator and value(s), matching one leaf in an <see cref="AlertConditionNode"/>
/// tree. Lives inside a <see cref="RuleConditionGroupViewModel"/>.
/// </summary>
public sealed class RuleConditionRowViewModel : ObservableObject
{
    private static readonly IReadOnlyList<AlertConditionField> AllFields =
        AlertConditionFields.Groups.SelectMany(g => g.Fields).ToList();

    private AlertConditionField _selectedField;
    private AlertConditionOperator? _selectedOperator;
    private IReadOnlyList<AlertConditionOperator> _operators;
    private string _value = string.Empty;
    private string _secondValue = string.Empty;
    private string _fieldSearchText;

    public RuleConditionRowViewModel(Action<RuleConditionRowViewModel> onRemove)
        : this(onRemove, null, null, Array.Empty<string>())
    {
    }

    public RuleConditionRowViewModel(Action<RuleConditionRowViewModel> onRemove, AlertConditionField? field, string? operatorValue, IReadOnlyList<string> values)
    {
        RemoveCommand = new RelayCommand(() => onRemove(this));

        _selectedField = field ?? AlertConditionFields.Resolve("devices.hostname");
        _operators = OperatorsFor(_selectedField, operatorValue);
        _selectedOperator = _operators.FirstOrDefault(o => o.Value == operatorValue) ?? _operators.FirstOrDefault();
        // in/not_in carry a whole list; it lives in the one value box as
        // comma-separated text (see ToNode for the reverse).
        _value = IsListOperator(operatorValue) ? string.Join(", ", values) : values.Count > 0 ? values[0] : string.Empty;
        _secondValue = values.Count > 1 ? values[1] : string.Empty;
        _fieldSearchText = _selectedField.Field;
    }

    /// <summary>Rebuilds this row from a leaf node, e.g. after an import.</summary>
    public static RuleConditionRowViewModel FromNode(AlertConditionNode leaf, Action<RuleConditionRowViewModel> onRemove) =>
        new(onRemove, AlertConditionFields.Resolve(leaf.Field ?? string.Empty), leaf.Operator, leaf.ValueList);

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
                OnPropertyChanged(nameof(ValueHint));

                _operators = OperatorsFor(value, null);
                OnPropertyChanged(nameof(Operators));

                if (SelectedOperator is null || !_operators.Any(o => o.Value == SelectedOperator.Value))
                {
                    SelectedOperator = _operators.FirstOrDefault();
                }
            }
        }
    }

    public IReadOnlyList<AlertConditionOperator> Operators => _operators;

    public AlertConditionOperator? SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (SetProperty(ref _selectedOperator, value))
            {
                OnPropertyChanged(nameof(HasValue));
                OnPropertyChanged(nameof(HasSecondValue));
            }
        }
    }

    /// <summary>False for is_null / is_empty style operators, which take no value.</summary>
    public bool HasValue => (SelectedOperator?.InputCount ?? 1) >= 1;

    /// <summary>True for between / not between, which take a lower and upper bound.</summary>
    public bool HasSecondValue => (SelectedOperator?.InputCount ?? 1) >= 2;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string SecondValue
    {
        get => _secondValue;
        set => SetProperty(ref _secondValue, value);
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

    public AlertConditionNode ToNode()
    {
        var op = SelectedOperator ?? Operators[0];

        return new AlertConditionNode
        {
            Id = SelectedField.Field,
            Field = SelectedField.Field,
            Type = SelectedField.Type,
            Input = SelectedField.Input,
            Operator = op.Value,
            Value = op.InputCount switch
            {
                0 => null,
                2 => AlertConditionNode.ArrayValue(new[] { Value, SecondValue }),
                _ when IsListOperator(op.Value) => AlertConditionNode.ArrayValue(Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                _ => AlertConditionNode.ScalarValue(Value),
            },
            Valid = true,
        };
    }

    /// <summary>
    /// The field's own operators, plus the rule's current operator as an
    /// extra entry when it isn't one of them (e.g. <c>in</c>, which LibreNMS
    /// stores but doesn't offer in its picker) - so opening a rule never
    /// silently swaps its operator for the first one in the list.
    /// </summary>
    private static bool IsListOperator(string? op) => op is "in" or "not_in";

    private static IReadOnlyList<AlertConditionOperator> OperatorsFor(AlertConditionField field, string? currentOperator)
    {
        if (currentOperator is null || field.Operators.Any(o => o.Value == currentOperator))
        {
            return field.Operators;
        }

        var inputCount = currentOperator.StartsWith("is_") ? 0 : 1;
        var extra = new AlertConditionOperator(currentOperator, currentOperator.Replace('_', ' '), inputCount);
        return field.Operators.Append(extra).ToList();
    }
}
