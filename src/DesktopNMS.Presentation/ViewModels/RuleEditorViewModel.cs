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
/// Backs the "Add/Edit rule" dialog (see <see cref="Views.RuleEditorWindow"/>),
/// mirroring LibreNMS's own rule editor modal: a Main tab (name, Import
/// from, the condition builder, severity, the Invert/Recovery/Acknowledgement
/// toggles, one combined "Match devices, groups and locations" list with its
/// "All devices except in list" switch, procedure URL and notes) and an
/// Advanced tab (Override SQL + query). One class serves both modes, same
/// shape as <see cref="LocationEditorViewModel"/> - create mode is the
/// default; <see cref="Initialize"/> switches it into edit mode.
/// </summary>
public sealed class RuleEditorViewModel : ObservableObject
{
    public const string DeviceKind = "Device";
    public const string GroupKind = "Group";
    public const string LocationKind = "Location";

    public static readonly IReadOnlyList<AlertSeverityOption> SeverityOptions = new[]
    {
        new AlertSeverityOption(AlertSeverity.Ok, "Ok"),
        new AlertSeverityOption(AlertSeverity.Warning, "Warning"),
        new AlertSeverityOption(AlertSeverity.Critical, "Critical"),
    };

    private readonly ILibreNmsClient _client;
    private readonly ILogger<RuleEditorViewModel> _logger;

    /// <summary>Null in create mode. In edit mode, the rule being edited - addresses the PUT and supplies the untouched builder when it couldn't be parsed.</summary>
    private AlertRule? _originalRule;

    // What the form was filled from: the rule being edited, or the one being
    // duplicated (#139) - whose targeting and condition carry over, though
    // saving creates a new rule.
    private AlertRule? _source;

    private string _name = string.Empty;
    private AlertSeverityOption _selectedSeverity = SeverityOptions[2];
    private string? _notes;
    private string? _procedure;
    private bool _disabled;
    private bool _invertMap;
    private bool _invert;
    private bool _recovery = true;
    private bool _acknowledgement = true;
    private bool _overrideQuery;
    private string? _advQuery;
    private bool _isAdvancedTabSelected;
    private bool _isConditionEditable = true;
    private string? _rawConditionJson;
    private bool _isImportOpen;
    private string _importText = string.Empty;
    private string? _importError;
    private AlertRule? _selectedImportRule;
    private bool _isBusy;
    private string? _errorMessage;
    private bool _notifyPerEntity;
    private string _maxEntitiesText = string.Empty;
    private IReadOnlyList<RemovedConditionField> _removedColumns = Array.Empty<RemovedConditionField>();
    private bool _reviewedRemovedColumns;

    public RuleEditorViewModel(ILibreNmsClient client, ILogger<RuleEditorViewModel> logger)
    {
        _client = client;
        _logger = logger;

        Root = new RuleConditionGroupViewModel();
        Root.ReplaceWith(new AlertConditionNode { Condition = "AND", Rules = new List<AlertConditionNode>() }); // one blank row to start

        MatchPicker = new CheckablePickerViewModel();
        ImportableRules = new BatchObservableCollection<AlertRule>();

        ToggleImportCommand = new RelayCommand(() => IsImportOpen = !IsImportOpen);
        ImportSqlCommand = new RelayCommand(() => Import(AlertRuleSqlImporter.Parse));
        ImportOldFormatCommand = new RelayCommand(() => Import(AlertRuleSqlImporter.ParseOldFormat));
        SelectMainTabCommand = new RelayCommand(() => IsAdvancedTabSelected = false);
        SelectAdvancedTabCommand = new RelayCommand(() => IsAdvancedTabSelected = true);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Name));

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
        _source = rule;
        Name = rule.Name ?? string.Empty;
        SelectedSeverity = SeverityOptions.FirstOrDefault(s => s.Value == rule.Severity) ?? SeverityOptions[2];
        Notes = rule.Notes;
        Procedure = rule.Procedure;
        Disabled = rule.Disabled;
        InvertMap = rule.InvertMap;

        var extra = rule.Extra;
        Invert = extra?.Invert ?? false;
        Recovery = extra?.Recovery ?? true;
        Acknowledgement = extra?.Acknowledgement ?? true;
        OverrideQuery = extra?.OverrideQuery ?? false;

        // Same as the web editor: the query box is pre-filled with whatever
        // SQL the rule currently runs, so ticking Override starts from it.
        AdvQuery = rule.Query;

        OnPropertyChanged(nameof(IsMuted));

        NotifyPerEntity = rule.NotifyPerEntity ?? false;
        MaxEntitiesText = rule.MaxEntities?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        OnPropertyChanged(nameof(SupportsPerEntity));

        LoadCondition(rule);
        CheckRemovedColumns();
    }

    /// <summary>
    /// Fills the dialog from an existing rule - conditions, severity,
    /// targeting, flags, override SQL, procedure and notes - named
    /// "... (copy)", and saves it as a new rule (#139).
    /// </summary>
    public void InitializeAsCopy(AlertRule rule)
    {
        Initialize(rule);
        _originalRule = null;
        Name = $"{rule.Name} (copy)";
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(SavePermission));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsMuted));
    }

    public bool IsEditMode => _originalRule is not null;

    /// <summary>What Save needs from the token (#51) - see <see cref="Views.PermissionGate"/>.</summary>
    public ApiPermission SavePermission => IsEditMode ? ApiPermission.EditRules : ApiPermission.CreateRules;

    public string Title => IsEditMode ? "Edit rule" : _source is not null ? "Duplicate rule" : "Add rule";

    public event EventHandler<bool>? RequestClose;

    // ------------------------------------------------------------------
    // Tabs

    public bool IsAdvancedTabSelected
    {
        get => _isAdvancedTabSelected;
        set
        {
            if (SetProperty(ref _isAdvancedTabSelected, value))
            {
                OnPropertyChanged(nameof(IsMainTabSelected));
            }
        }
    }

    public bool IsMainTabSelected => !IsAdvancedTabSelected;

    public RelayCommand SelectMainTabCommand { get; }

    public RelayCommand SelectAdvancedTabCommand { get; }

    // ------------------------------------------------------------------
    // Main tab

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

    /// <summary>"All devices except in list" - run against every device NOT in the match list.</summary>
    public bool InvertMap
    {
        get => _invertMap;
        set => SetProperty(ref _invertMap, value);
    }

    /// <summary>"Invert rule match" - alert when the condition does NOT match.</summary>
    public bool Invert
    {
        get => _invert;
        set => SetProperty(ref _invert, value);
    }

    /// <summary>"Recovery alerts".</summary>
    public bool Recovery
    {
        get => _recovery;
        set => SetProperty(ref _recovery, value);
    }

    /// <summary>"Acknowledgement alerts".</summary>
    public bool Acknowledgement
    {
        get => _acknowledgement;
        set => SetProperty(ref _acknowledgement, value);
    }

    /// <summary>
    /// True when the rule being edited carries LibreNMS's legacy "Mute
    /// alerts" flag. The API's save handler has no field for it and drops it
    /// on every write, so the view shows a warning rather than a toggle.
    /// </summary>
    public bool IsMuted => _originalRule?.Extra?.Mute ?? false;

    /// <summary>The root AND/OR group of the condition tree.</summary>
    public RuleConditionGroupViewModel Root { get; }

    /// <summary>
    /// Drag-to-reorder: moves <paramref name="dragged"/> (a row or a group)
    /// next to <paramref name="target"/> - after it when <paramref name="placeAfter"/>,
    /// otherwise before - or, when <paramref name="target"/> is a group and
    /// <paramref name="intoGroup"/> is set, to the top of that group. The
    /// moved item is rebuilt in its new group (see
    /// <see cref="RuleConditionGroupViewModel.Adopt"/>) rather than re-parented.
    /// Returns false for a no-op or an illegal move (a group into itself, or
    /// one that would leave the root empty).
    /// </summary>
    public bool MoveCondition(object dragged, object target, bool placeAfter, bool intoGroup)
    {
        if (ReferenceEquals(dragged, target))
        {
            return false;
        }

        var source = Root.FindOwner(dragged);
        if (source is null)
        {
            return false;
        }

        RuleConditionGroupViewModel destination;
        int index;

        if (intoGroup && target is RuleConditionGroupViewModel targetGroup)
        {
            destination = targetGroup;
            index = 0;
        }
        else
        {
            var owner = Root.FindOwner(target);
            if (owner is null)
            {
                return false;
            }

            destination = owner;
            index = owner.Children.IndexOf(target) + (placeAfter ? 1 : 0);
        }

        if (dragged is RuleConditionGroupViewModel draggedGroup && destination.IsWithin(draggedGroup))
        {
            return false;
        }

        // Moving the root's only child somewhere else would empty the root;
        // reordering a group's only child within itself is a no-op (and
        // Detach would prune the group out from under the insert).
        if (source.Children.Count == 1 && (source.IsRoot || ReferenceEquals(destination, source)))
        {
            return false;
        }

        var node = dragged switch
        {
            RuleConditionRowViewModel row => row.ToNode(),
            RuleConditionGroupViewModel group => group.ToNode(),
            _ => null,
        };

        if (node is null)
        {
            return false;
        }

        // Removing first shifts later indexes in the same group down by one.
        if (ReferenceEquals(source, destination) && source.Children.IndexOf(dragged) < index)
        {
            index--;
        }

        source.Detach(dragged);

        // Detach may have pruned an emptied sub-group that was the destination.
        if (!destination.IsRoot && Root.FindOwner(destination) is null)
        {
            return false;
        }

        index = Math.Clamp(index, 0, destination.Children.Count);
        destination.Children.Insert(index, destination.Adopt(node));
        return true;
    }

    /// <summary>False only when the stored builder JSON couldn't be parsed at all - shows <see cref="RawConditionJson"/> instead.</summary>
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

    /// <summary>One combined, searchable list of every device, group and location, each tagged with its kind.</summary>
    public CheckablePickerViewModel MatchPicker { get; }

    // ------------------------------------------------------------------
    // Import from

    public bool IsImportOpen
    {
        get => _isImportOpen;
        set => SetProperty(ref _isImportOpen, value);
    }

    public RelayCommand ToggleImportCommand { get; }

    /// <summary>SQL (bare WHERE clause or a full SELECT) or an old-format rule, depending on which import button is pressed.</summary>
    public string ImportText
    {
        get => _importText;
        set
        {
            if (SetProperty(ref _importText, value ?? string.Empty))
            {
                ImportError = null;
            }
        }
    }

    public string? ImportError
    {
        get => _importError;
        private set
        {
            if (SetProperty(ref _importError, value))
            {
                OnPropertyChanged(nameof(HasImportError));
            }
        }
    }

    public bool HasImportError => !string.IsNullOrEmpty(_importError);

    public RelayCommand ImportSqlCommand { get; }

    public RelayCommand ImportOldFormatCommand { get; }

    /// <summary>Every other rule on the server, for "Import from → Alert Rule".</summary>
    public BatchObservableCollection<AlertRule> ImportableRules { get; }

    /// <summary>Picking a rule copies its conditions in, then clears the selection so the same rule can be picked again.</summary>
    public AlertRule? SelectedImportRule
    {
        get => _selectedImportRule;
        set
        {
            if (value is null || !SetProperty(ref _selectedImportRule, value))
            {
                return;
            }

            ImportFromRule(value);
            _selectedImportRule = null;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------
    // Advanced tab

    /// <summary>"Override SQL" - run <see cref="AdvQuery"/> instead of the SQL LibreNMS derives from the builder.</summary>
    public bool OverrideQuery
    {
        get => _overrideQuery;
        set => SetProperty(ref _overrideQuery, value);
    }

    public string? AdvQuery
    {
        get => _advQuery;
        set => SetProperty(ref _advQuery, value);
    }

    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // LibreNMS 26.10 (#350)

    /// <summary>The server's LibreNMS version, once known - see <see cref="ApiPermissions.ServerVersion"/>.</summary>
    public Version? ServerVersion => _client.Permissions.ServerVersion;

    /// <summary>LibreNMS 26.10 or later, or a rule that already carries the settings.</summary>
    public bool SupportsPerEntity => ServerVersion >= LibreNmsVersion.Faults || _source?.NotifyPerEntity is not null;

    /// <summary>"Notify per entity": one alert per matching port, sensor or core, rather than one per device.</summary>
    public bool NotifyPerEntity
    {
        get => _notifyPerEntity;
        set
        {
            if (SetProperty(ref _notifyPerEntity, value))
            {
                OnPropertyChanged(nameof(PerEntityExplanation));
            }
        }
    }

    /// <summary>"Max entities" - blank inherits the server's default (20, unless changed in LibreNMS).</summary>
    public string MaxEntitiesText
    {
        get => _maxEntitiesText;
        set
        {
            if (SetProperty(ref _maxEntitiesText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(PerEntityExplanation));
            }
        }
    }

    /// <summary>What the two settings mean for alerts and acknowledging.</summary>
    public string PerEntityExplanation
    {
        get
        {
            if (!NotifyPerEntity)
            {
                return "Off: LibreNMS raises one alert per device for this rule, covering every matching port, sensor or core, and acknowledging it acknowledges them all.";
            }

            var limit = int.TryParse(MaxEntitiesText.Trim(), out var value) && value >= 1
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "the server default (20, unless changed in LibreNMS)";
            return $"On: one alert per matching port, sensor or core while a device has no more than {limit} open. Beyond that, LibreNMS groups them into one alert for the device, and acknowledging any of them acknowledges them all.";
        }
    }

    /// <summary>Columns this rule's conditions or SQL use that LibreNMS 26.10 removed - shown for review before saving.</summary>
    public IReadOnlyList<RemovedConditionField> RemovedColumns
    {
        get => _removedColumns;
        private set
        {
            if (SetProperty(ref _removedColumns, value))
            {
                OnPropertyChanged(nameof(HasRemovedColumns));
                OnPropertyChanged(nameof(RemovedColumnsText));
            }
        }
    }

    public bool HasRemovedColumns => _removedColumns.Count > 0;

    public string RemovedColumnsText => string.Join(Environment.NewLine, _removedColumns.Select(c => $"{c.Field}: {c.Advice}"));

    /// <summary>Ticked once the flagged conditions have been looked at - saving needs it. Custom SQL is never rewritten.</summary>
    public bool ReviewedRemovedColumns
    {
        get => _reviewedRemovedColumns;
        set => SetProperty(ref _reviewedRemovedColumns, value);
    }

    /// <summary>Re-checks the conditions and SQL for removed columns - after loading, importing, or learning the server's version.</summary>
    private void CheckRemovedColumns()
    {
        var builder = IsConditionEditable ? JsonSerializer.Serialize(Root.ToNode()) : _source?.Builder;
        var sql = OverrideQuery ? AdvQuery : null;
        RemovedColumns = AlertRuleCompatibility.FindRemovedColumns(builder, sql, ServerVersion ?? LibreNmsVersion.Faults);
    }

    private void RefreshForServerVersion()
    {
        RuleConditionRowViewModel.ServerVersion = ServerVersion;
        foreach (var row in Rows(Root))
        {
            row.RefreshForServerVersion();
        }

        OnPropertyChanged(nameof(ServerVersion));
        OnPropertyChanged(nameof(SupportsPerEntity));
        CheckRemovedColumns();
    }

    private static IEnumerable<RuleConditionRowViewModel> Rows(RuleConditionGroupViewModel group)
    {
        foreach (var child in group.Children)
        {
            if (child is RuleConditionRowViewModel row)
            {
                yield return row;
            }
            else if (child is RuleConditionGroupViewModel inner)
            {
                foreach (var nested in Rows(inner))
                {
                    yield return nested;
                }
            }
        }
    }
    private void LoadCondition(AlertRule rule)
    {
        AlertConditionNode? tree = null;

        if (!string.IsNullOrWhiteSpace(rule.Builder))
        {
            try
            {
                tree = JsonSerializer.Deserialize<AlertConditionNode>(rule.Builder);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Rule {RuleId}'s builder JSON could not be parsed; showing it read-only", rule.Id);
            }
        }

        if (tree is null)
        {
            IsConditionEditable = false;
            RawConditionJson = rule.Builder;
            return;
        }

        IsConditionEditable = true;
        RawConditionJson = null;
        Root.ReplaceWith(tree);
    }

    private void Import(Func<string, AlertConditionNode> parse)
    {
        if (string.IsNullOrWhiteSpace(ImportText))
        {
            ImportError = "Paste a query first.";
            return;
        }

        try
        {
            Root.ReplaceWith(parse(ImportText));
            IsConditionEditable = true;
            RawConditionJson = null;
            ImportError = null;
            IsImportOpen = false;
            ImportText = string.Empty;
            CheckRemovedColumns();
        }
        catch (FormatException ex)
        {
            ImportError = ex.Message;
        }
    }

    private void ImportFromRule(AlertRule source)
    {
        if (string.IsNullOrWhiteSpace(source.Builder))
        {
            ImportError = $"\"{source.Name}\" has no builder conditions to copy.";
            return;
        }

        try
        {
            var tree = JsonSerializer.Deserialize<AlertConditionNode>(source.Builder);
            if (tree is null)
            {
                ImportError = $"\"{source.Name}\"'s conditions couldn't be read.";
                return;
            }

            Root.ReplaceWith(tree);
            IsConditionEditable = true;
            RawConditionJson = null;
            ImportError = null;
            IsImportOpen = false;
            CheckRemovedColumns();
        }
        catch (JsonException)
        {
            ImportError = $"\"{source.Name}\"'s conditions couldn't be read.";
        }
    }

    private async Task LoadPickerDataAsync()
    {
        RuleConditionRowViewModel.ServerVersion = ServerVersion;
        if (ServerVersion is null)
        {
            try
            {
                // Records the version on the connection's permissions as a side effect.
                await _client.System.GetAsync().ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogDebug(ex, "Could not read the server's version for the rule editor");
            }
        }

        RefreshForServerVersion();

        try
        {
            var devicesTask = _client.Devices.ListAsync();
            var groupsTask = _client.DeviceGroups.ListAsync();
            var locationsTask = _client.Locations.ListAsync();
            var rulesTask = _client.Rules.ListAsync();
            await Task.WhenAll(devicesTask, groupsTask, locationsTask, rulesTask).ConfigureAwait(true);

            MatchPicker.Items.Clear();
            MatchPicker.Add(DeviceKind, devicesTask.Result.Select(d => (d.DeviceId, d.BestName)), (_source?.Devices ?? new List<int>()).ToHashSet());
            MatchPicker.Add(GroupKind, groupsTask.Result.Select(g => (g.Id, g.Name)), (_source?.Groups ?? new List<int>()).ToHashSet());
            MatchPicker.Add(LocationKind, locationsTask.Result.Select(l => (l.Id, l.Name)), (_source?.Locations ?? new List<int>()).ToHashSet());

            using (ImportableRules.BeginBatch())
            {
                ImportableRules.Clear();
                foreach (var rule in rulesTask.Result.Where(r => r.Id != _originalRule?.Id).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    ImportableRules.Add(rule);
                }
            }
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
            // The API validates the builder before it looks at the override,
            // so it's always sent - the untouched original when it couldn't
            // be parsed into the tree editor.
            var builderJson = IsConditionEditable
                ? JsonSerializer.Serialize(Root.ToNode())
                : _source?.Builder ?? string.Empty;

            var devices = MatchPicker.CheckedIdsOf(DeviceKind).ToList();
            if (devices.Count == 0)
            {
                devices.Add(-1); // "global" - see AlertRuleWriteRequest
            }

            var request = new AlertRuleWriteRequest
            {
                RuleId = _originalRule?.Id,
                Name = Name,
                Severity = SelectedSeverity.Value.ToApiValue() ?? "critical",
                Builder = builderJson,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes,
                Procedure = string.IsNullOrWhiteSpace(Procedure) ? null : Procedure,
                Disabled = Disabled ? 1 : 0,
                InvertMap = InvertMap,
                Invert = Invert,
                Recovery = Recovery,
                Acknowledgement = Acknowledgement,
                OverrideQuery = OverrideQuery,
                AdvQuery = OverrideQuery ? AdvQuery : null,
                Devices = devices,
                Groups = MatchPicker.CheckedIdsOf(GroupKind).ToList(),
                Locations = MatchPicker.CheckedIdsOf(LocationKind).ToList(),
            };

            if (OverrideQuery && string.IsNullOrWhiteSpace(AdvQuery))
            {
                ErrorMessage = "Override SQL is on but the query is empty - enter the SQL to run, or turn the override off.";
                return;
            }

            CheckRemovedColumns();
            if (HasRemovedColumns && !ReviewedRemovedColumns)
            {
                ErrorMessage = "This rule uses columns LibreNMS 26.10 removed. Review the conditions flagged above, then tick 'I've reviewed these' to save.";
                return;
            }

            if (SupportsPerEntity)
            {
                int? maxEntities = null;
                if (!string.IsNullOrWhiteSpace(MaxEntitiesText))
                {
                    if (!int.TryParse(MaxEntitiesText.Trim(), out var limit) || limit < 1)
                    {
                        ErrorMessage = "Max entities must be a whole number of 1 or more, or blank to use the server's default.";
                        return;
                    }

                    maxEntities = limit;
                }

                request.SetPerEntity(_originalRule, NotifyPerEntity, maxEntities);
            }

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
