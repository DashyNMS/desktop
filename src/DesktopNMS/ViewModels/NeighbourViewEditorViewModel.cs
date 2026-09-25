using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>A choice in one of the editor's drop-downs - named by <see cref="ToString"/>, which the app's ComboBox template shows.</summary>
public sealed record EditorChoice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The "New view" / "Edit view" dialog for the Neighbours tab: a name, rules
/// and whether all or any must match, and whether the view's neighbours go
/// on the network map - with a live count of what the rules match right
/// now, so a view can be tuned before it's saved.
/// </summary>
public sealed class NeighbourViewEditorViewModel : ObservableObject
{
    private const int PreviewNames = 6;

    private readonly INeighbourDirectory _directory;
    private readonly IDeviceCache _devices;
    private readonly DispatcherTimer _previewTimer;

    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private bool _matchAll = true;
    private bool _showOnMap;
    private bool _isEditMode;
    private NeighbourSnapshot? _snapshot;
    private string _previewText = "Checking...";
    private string? _errorMessage;

    public NeighbourViewEditorViewModel(INeighbourDirectory directory, IDeviceCache devices)
    {
        _directory = directory;
        _devices = devices;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreview();
        };

        Rules = new ObservableCollection<NeighbourRuleRowViewModel>();
        AddRuleCommand = new RelayCommand(() => AddRule(new NeighbourRule()));
        SaveCommand = new RelayCommand(Save);

        AddRule(new NeighbourRule());
        _ = LoadSnapshotAsync();
    }

    /// <summary>True to save, false to cancel.</summary>
    public event EventHandler<bool>? RequestClose;

    public static IReadOnlyList<EditorChoice<NeighbourRuleField>> FieldChoices { get; } =
        Enum.GetValues<NeighbourRuleField>().Select(f => new EditorChoice<NeighbourRuleField>(f, NeighbourViewText.FieldName(f))).ToList();

    public static IReadOnlyList<EditorChoice<NeighbourRuleOperator>> OperatorChoices { get; } =
        Enum.GetValues<NeighbourRuleOperator>().Select(o => new EditorChoice<NeighbourRuleOperator>(o, NeighbourViewText.OperatorName(o))).ToList();

    public string Title => _isEditMode ? "Edit neighbour view" : "New neighbour view";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool MatchAll
    {
        get => _matchAll;
        set
        {
            if (SetProperty(ref _matchAll, value))
            {
                OnPropertyChanged(nameof(MatchAny));
                SchedulePreview();
            }
        }
    }

    /// <summary>The "any rule" radio button - the other side of <see cref="MatchAll"/>.</summary>
    public bool MatchAny
    {
        get => !_matchAll;
        set => MatchAll = !value;
    }

    public bool ShowOnMap
    {
        get => _showOnMap;
        set => SetProperty(ref _showOnMap, value);
    }

    public ObservableCollection<NeighbourRuleRowViewModel> Rules { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand SaveCommand { get; }


    /// <summary>"Matches 37 neighbours: Studio Antenna 02, ..." - what the rules pick out right now.</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
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

    /// <summary>What was saved - set just before <see cref="RequestClose"/> with true.</summary>
    public NeighbourViewDefinition? Result { get; private set; }

    /// <summary>Edit mode: starts from a copy of <paramref name="existing"/>, saved back under the same id.</summary>
    public void Initialize(NeighbourViewDefinition existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        _isEditMode = true;
        _id = existing.Id;
        Name = existing.Name;
        MatchAll = existing.MatchAll;
        ShowOnMap = existing.ShowOnMap;

        foreach (var row in Rules.ToList())
        {
            RemoveRule(row);
        }

        foreach (var rule in existing.Rules)
        {
            AddRule(rule.Clone());
        }

        if (Rules.Count == 0)
        {
            AddRule(new NeighbourRule());
        }

        OnPropertyChanged(nameof(Title));
        SchedulePreview();
    }

    private NeighbourViewDefinition Build() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        MatchAll = MatchAll,
        ShowOnMap = ShowOnMap,
        Rules = Rules.Select(r => r.ToRule()).Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList(),
    };

    private void AddRule(NeighbourRule rule)
    {
        var row = new NeighbourRuleRowViewModel(rule, RemoveRule, SchedulePreview);
        Rules.Add(row);
        SchedulePreview();
    }

    private void RemoveRule(NeighbourRuleRowViewModel row)
    {
        Rules.Remove(row);
        SchedulePreview();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Give the view a name.";
            return;
        }

        if (!Rules.Any(r => !string.IsNullOrWhiteSpace(r.Value)))
        {
            ErrorMessage = "Add at least one rule with something to look for.";
            return;
        }

        if (Rules.FirstOrDefault(r => r.HasRegexProblem) is { } bad)
        {
            ErrorMessage = $"The regex \"{bad.Value}\" isn't valid: {bad.RegexProblem}";
            return;
        }

        Result = Build();
        RequestClose?.Invoke(this, true);
    }

    private async Task LoadSnapshotAsync()
    {
        try
        {
            _snapshot = await _directory.GetAsync().ConfigureAwait(true);
            UpdatePreview();
        }
        catch (LibreNmsApiException ex)
        {
            PreviewText = "Couldn't load neighbours to preview: " + ex.ToUserMessage();
        }
    }

    private void SchedulePreview()
    {
        ErrorMessage = null;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void UpdatePreview()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var view = Build();
        if (view.Rules.Count == 0)
        {
            PreviewText = "Add a rule to see what it matches.";
            return;
        }

        var matches = snapshot.For(view, id => _devices.Get(id)?.BestName);
        if (matches.Count == 0)
        {
            PreviewText = "Matches nothing your switches see right now.";
            return;
        }

        var names = matches.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shown = string.Join(", ", names.Take(PreviewNames));
        var more = names.Count > PreviewNames ? $" and {names.Count - PreviewNames} more" : string.Empty;
        PreviewText = string.Create(CultureInfo.CurrentCulture, $"Matches {matches.Count} {(matches.Count == 1 ? "neighbour" : "neighbours")}: {shown}{more}");
    }
}

/// <summary>One rule row in the view editor.</summary>
public sealed class NeighbourRuleRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private EditorChoice<NeighbourRuleField> _field;
    private EditorChoice<NeighbourRuleOperator> _operator;
    private string _value;

    public NeighbourRuleRowViewModel(NeighbourRule rule, Action<NeighbourRuleRowViewModel> remove, Action changed)
    {
        _changed = changed;
        _field = NeighbourViewEditorViewModel.FieldChoices.First(c => c.Value == rule.Field);
        _operator = NeighbourViewEditorViewModel.OperatorChoices.First(c => c.Value == rule.Operator);
        _value = rule.Value;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public EditorChoice<NeighbourRuleField> Field
    {
        get => _field;
        set
        {
            if (value is not null && SetProperty(ref _field, value))
            {
                _changed();
            }
        }
    }

    public EditorChoice<NeighbourRuleOperator> Operator
    {
        get => _operator;
        set
        {
            if (value is not null && SetProperty(ref _operator, value))
            {
                OnPropertyChanged(nameof(RegexProblem));
                OnPropertyChanged(nameof(HasRegexProblem));
                _changed();
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(RegexProblem));
                OnPropertyChanged(nameof(HasRegexProblem));
                _changed();
            }
        }
    }

    public string? RegexProblem => _operator.Value == NeighbourRuleOperator.Matches && !string.IsNullOrWhiteSpace(_value)
        ? Neighbours.RegexProblem(_value.Trim())
        : null;

    public bool HasRegexProblem => RegexProblem is not null;

    public RelayCommand RemoveCommand { get; }

    public NeighbourRule ToRule() => new() { Field = _field.Value, Operator = _operator.Value, Value = _value.Trim() };
}
