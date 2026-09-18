using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>One selectable severity in the rule editor - wraps <see cref="AlertSeverity"/> with a ToString() override, the established workaround for this app's ComboBox closed-display quirk (see GraphType/DeviceNameOption).</summary>
public sealed class AlertSeverityOption
{
    public AlertSeverityOption(AlertSeverity value, string label)
    {
        Value = value;
        Label = label;
    }

    public AlertSeverity Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// Backs the "Add/Edit rule" dialog (see <see cref="Views.RuleEditorWindow"/>).
/// One class serves both modes, same shape as <see cref="LocationEditorViewModel"/>
/// - create mode is the default; <see cref="Initialize"/> switches it into
/// edit mode for a specific existing rule. The condition builder only edits a
/// single flat AND/OR group of leaf conditions (issue #22) - a rule whose
/// builder nests a group inside a group falls back to a read-only raw-JSON
/// view (<see cref="IsConditionEditable"/>) rather than risk flattening or
/// corrupting a structure this editor doesn't model.
/// </summary>
public sealed class RuleEditorViewModel : ObservableObject
{
    public static readonly IReadOnlyList<AlertSeverityOption> SeverityOptions = new[]
    {
        new AlertSeverityOption(AlertSeverity.Ok, "Ok"),
        new AlertSeverityOption(AlertSeverity.Warning, "Warning"),
        new AlertSeverityOption(AlertSeverity.Critical, "Critical"),
    };

    private readonly ILibreNmsClient _client;
    private readonly ILogger<RuleEditorViewModel> _logger;

    /// <summary>Null in create mode. In edit mode, the rule being edited - addresses the PUT and carries forward escalation fields this editor doesn't expose.</summary>
    private AlertRule? _originalRule;

    private string _name = string.Empty;
    private AlertSeverityOption _selectedSeverity = SeverityOptions[2];
    private string? _notes;
    private string? _procedure;
    private bool _disabled;
    private bool _invertMap;
    private string _topLevelOperator = "AND";
    private bool _isConditionEditable = true;
    private string? _rawConditionJson;
    private bool _isBusy;
    private string? _errorMessage;

    public RuleEditorViewModel(ILibreNmsClient client, ILogger<RuleEditorViewModel> logger)
    {
        _client = client;
        _logger = logger;

        ConditionRows = new ObservableCollection<RuleConditionRowViewModel>();
        DevicesPicker = new CheckablePickerViewModel();
        GroupsPicker = new CheckablePickerViewModel();
        LocationsPicker = new CheckablePickerViewModel();

        AddConditionCommand = new RelayCommand(() => ConditionRows.Add(new RuleConditionRowViewModel(RemoveConditionRow)));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Name));

        ConditionRows.Add(new RuleConditionRowViewModel(RemoveConditionRow));

        _ = LoadPickerDataAsync();
    }

    /// <summary>
    /// Switches this dialog into edit mode for an existing rule - called by
    /// <see cref="Services.WindowService.ShowEditRuleDialog"/> right after
    /// resolving this view model from DI and before the dialog is shown. Safe
    /// to call before <see cref="LoadPickerDataAsync"/> has finished - same
    /// reasoning as <see cref="DeviceGroupEditorViewModel.Initialize"/>.
    /// </summary>
    public void Initialize(AlertRule rule)
    {
        _originalRule = rule;
        Name = rule.Name ?? string.Empty;
        SelectedSeverity = SeverityOptions.FirstOrDefault(s => s.Value == rule.Severity) ?? SeverityOptions[2];
        Notes = rule.Notes;
        Procedure = rule.Procedure;
        Disabled = rule.Disabled;
        InvertMap = rule.InvertMap;

        LoadCondition(rule);
    }

    public bool IsEditMode => _originalRule is not null;

    public string Title => IsEditMode ? "Edit rule" : "Add rule";

    public event EventHandler<bool>? RequestClose;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<AlertSeverityOption> Severities => SeverityOptions;

    public AlertSeverityOption SelectedSeverity
    {
        get => _selectedSeverity;
        set => SetProperty(ref _selectedSeverity, value);
    }

    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public string? Procedure
    {
        get => _procedure;
        set => SetProperty(ref _procedure, value);
    }

    public bool Disabled
    {
        get => _disabled;
        set => SetProperty(ref _disabled, value);
    }

    public bool InvertMap
    {
        get => _invertMap;
        set => SetProperty(ref _invertMap, value);
    }

    /// <summary>"AND" or "OR" - how the top-level condition group combines its rows.</summary>
    public string TopLevelOperator
    {
        get => _topLevelOperator;
        set
        {
            if (SetProperty(ref _topLevelOperator, value))
            {
                OnPropertyChanged(nameof(IsTopLevelAnd));
                OnPropertyChanged(nameof(IsTopLevelOr));
            }
        }
    }

    public bool IsTopLevelAnd
    {
        get => TopLevelOperator == "AND";
        set { if (value) TopLevelOperator = "AND"; }
    }

    public bool IsTopLevelOr
    {
        get => TopLevelOperator == "OR";
        set { if (value) TopLevelOperator = "OR"; }
    }

    public ObservableCollection<RuleConditionRowViewModel> ConditionRows { get; }

    public RelayCommand AddConditionCommand { get; }

    /// <summary>False when the source rule's builder nests a group inside a group - shows <see cref="RawConditionJson"/> instead.</summary>
    public bool IsConditionEditable
    {
        get => _isConditionEditable;
        private set => SetProperty(ref _isConditionEditable, value);
    }

    public string? RawConditionJson
    {
        get => _rawConditionJson;
        private set => SetProperty(ref _rawConditionJson, value);
    }

    public CheckablePickerViewModel DevicesPicker { get; }

    public CheckablePickerViewModel GroupsPicker { get; }

    public CheckablePickerViewModel LocationsPicker { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    private void RemoveConditionRow(RuleConditionRowViewModel row)
    {
        if (ConditionRows.Count > 1)
        {
            ConditionRows.Remove(row);
        }
    }

    private void LoadCondition(AlertRule rule)
    {
        ConditionRows.Clear();

        AlertConditionNode? tree = null;

        if (!string.IsNullOrWhiteSpace(rule.Builder))
        {
            try
            {
                tree = JsonSerializer.Deserialize<AlertConditionNode>(rule.Builder);
            }
            catch (JsonException)
            {
                tree = null;
            }
        }

        if (tree is null || !tree.IsFlatGroup)
        {
            IsConditionEditable = false;
            RawConditionJson = rule.Builder;
            ConditionRows.Add(new RuleConditionRowViewModel(RemoveConditionRow));
            return;
        }

        IsConditionEditable = true;
        RawConditionJson = null;
        TopLevelOperator = string.Equals(tree.Condition, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";

        foreach (var leaf in tree.Rules!)
        {
            var field = AlertConditionFields.Resolve(leaf.Field ?? string.Empty);
            ConditionRows.Add(new RuleConditionRowViewModel(RemoveConditionRow, field, leaf.Operator, leaf.Value));
        }

        if (ConditionRows.Count == 0)
        {
            ConditionRows.Add(new RuleConditionRowViewModel(RemoveConditionRow));
        }
    }

    private async Task LoadPickerDataAsync()
    {
        try
        {
            var devicesTask = _client.Devices.ListAsync();
            var groupsTask = _client.DeviceGroups.ListAsync();
            var locationsTask = _client.Locations.ListAsync();
            await Task.WhenAll(devicesTask, groupsTask, locationsTask).ConfigureAwait(true);

            var checkedDevices = (_originalRule?.Devices ?? new List<int>()).ToHashSet();
            var checkedGroups = (_originalRule?.Groups ?? new List<int>()).ToHashSet();
            var checkedLocations = (_originalRule?.Locations ?? new List<int>()).ToHashSet();

            DevicesPicker.Load(devicesTask.Result.Select(d => (d.DeviceId, d.BestName)), checkedDevices);
            GroupsPicker.Load(groupsTask.Result.Select(g => (g.Id, g.Name)), checkedGroups);
            LocationsPicker.Load(locationsTask.Result.Select(l => (l.Id, l.Name)), checkedLocations);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load devices/groups/locations for the rule editor");
            ErrorMessage = ex.ToUserMessage();
        }
    }

    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;

        try
        {
            string builderJson;

            if (IsConditionEditable)
            {
                var node = new AlertConditionNode
                {
                    Condition = TopLevelOperator,
                    Rules = ConditionRows.Select(r => r.ToNode()).ToList(),
                    Valid = true,
                };
                builderJson = JsonSerializer.Serialize(node);
            }
            else
            {
                // Never touched by this editor - preserved exactly as loaded.
                builderJson = _originalRule?.Builder ?? string.Empty;
            }

            var request = new AlertRuleWriteRequest
            {
                RuleId = _originalRule?.Id,
                Name = Name,
                Severity = SelectedSeverity.Value.ToApiValue() ?? "critical",
                Builder = builderJson,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                Procedure = string.IsNullOrWhiteSpace(Procedure) ? null : Procedure,
                Disabled = Disabled,
                InvertMap = InvertMap,
                Devices = DevicesPicker.CheckedIds.ToList(),
                Groups = GroupsPicker.CheckedIds.ToList(),
                Locations = LocationsPicker.CheckedIds.ToList(),
                AlertOperationId = _originalRule?.AlertOperationId,
                Operations = _originalRule?.Operations,
                DefaultOperationStepDurationSeconds = _originalRule?.DefaultOperationStepDurationSeconds,
            };

            if (_originalRule is not null)
            {
                await _client.Rules.UpdateAsync(request).ConfigureAwait(true);
            }
            else
            {
                await _client.Rules.CreateAsync(request).ConfigureAwait(true);
            }

            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not save alert rule {RuleName}", Name);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
