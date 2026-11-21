using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One agent provider's details in Settings › Providers (#316): where it
/// stands and how to fix it, which executable to use, and the model and
/// reasoning a new chat starts with. The choices are part of Settings'
/// draft, saved with everything else; the status is the live provider's.
/// </summary>
public sealed class IntelligenceAgentSettingsViewModel : ObservableObject
{
    private readonly AppSettings _draft;

    internal IntelligenceAgentSettingsViewModel(IntelligenceProviderViewModel provider, AppSettings draft)
    {
        Provider = provider;
        _draft = draft;
        BrowseCommand = new RelayCommand(Browse);
        CopySetupCommand = new RelayCommand(() =>
        {
            if (Provider.SetupCommandText is { } command) UiPlatform.Clipboard.SetText(command);
        }, () => Provider.HasSetupCommand);
        CheckCommand = new AsyncRelayCommand(() =>
        {
            // A path that isn't saved yet is checked on the live provider; Detach puts the saved one back if it's never applied.
            _checkedUnsaved |= !string.Equals(ExecutablePath, _savedPath, StringComparison.Ordinal);
            return Provider.CheckAsync(CancellationToken.None, ExecutablePath);
        }, () => !Provider.IsChecking);
        _savedPath = ExecutablePath;
        ((INotifyPropertyChanged)Provider).PropertyChanged += OnProviderChanged;
        Provider.Models.CollectionChanged += OnModelsChanged;
    }

    private readonly string? _savedPath;
    private bool _checkedUnsaved;

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IntelligenceProviderViewModel.Status) or nameof(IntelligenceProviderViewModel.IsChecking))
        {
            CopySetupCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnModelsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RaiseDefaults();

    /// <summary>
    /// Settings is closing: stops following the live provider, and if an
    /// executable was checked but never applied, checks the saved one again
    /// so the provider is back as it was.
    /// </summary>
    internal void Detach(string? appliedPath)
    {
        ((INotifyPropertyChanged)Provider).PropertyChanged -= OnProviderChanged;
        Provider.Models.CollectionChanged -= OnModelsChanged;
        if (_checkedUnsaved && string.Equals(appliedPath, _savedPath, StringComparison.Ordinal)) _ = Provider.CheckAsync(CancellationToken.None);
    }

    /// <summary>
    /// Offers the defaults again: shown after its models, a list can drop a
    /// selection that arrived first, so Settings re-sends it once the provider is on screen.
    /// </summary>
    internal void RaiseDefaults()
    {
        OnPropertyChanged(nameof(DefaultModel));
        OnPropertyChanged(nameof(DefaultEfforts));
        OnPropertyChanged(nameof(HasDefaultEfforts));
        OnPropertyChanged(nameof(DefaultEffort));
        OnPropertyChanged(nameof(DefaultEffortIndex));
    }

    /// <summary>The live provider: status, version, account, models.</summary>
    public IntelligenceProviderViewModel Provider { get; }

    public RelayCommand BrowseCommand { get; }

    public RelayCommand CopySetupCommand { get; }

    /// <summary>Checks with the executable as entered here, before it's saved.</summary>
    public AsyncRelayCommand CheckCommand { get; }

    private IntelligenceProviderSettings Chosen
    {
        get
        {
            if (!_draft.IntelligenceProviders.TryGetValue(Provider.Id, out var chosen))
            {
                chosen = new IntelligenceProviderSettings();
                _draft.IntelligenceProviders[Provider.Id] = chosen;
            }

            return chosen;
        }
    }

    /// <summary>Empty to find the installed agent; a path when it's somewhere DashyNMS doesn't look.</summary>
    public string? ExecutablePath
    {
        get => Chosen.ExecutablePath;
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
            if (Chosen.ExecutablePath == path) return;
            Chosen.ExecutablePath = path;
            OnPropertyChanged();
        }
    }

    /// <summary>The model new chats start with on this provider; the first listed when unset.</summary>
    public string? DefaultModel
    {
        get => Chosen.Model ?? Provider.Models.FirstOrDefault()?.Id;
        set
        {
            if (value is null || Chosen.Model == value) return;
            Chosen.Model = value;
            if (Chosen.Effort is { } effort && !DefaultEfforts.Any(e => e.Key == effort)) Chosen.Effort = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultEfforts));
            OnPropertyChanged(nameof(DefaultEffort));
            OnPropertyChanged(nameof(DefaultEffortIndex));
        }
    }

    private (string? Model, IReadOnlyList<KeyValuePair<string, string>> Levels) _efforts;

    /// <summary>
    /// The default model's reasoning levels, with "the model's default" first.
    /// The same list until the model changes: a new one each time would clear
    /// the list's selection.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> DefaultEfforts
    {
        get
        {
            var id = DefaultModel;
            var model = Provider.Models.FirstOrDefault(m => m.Id == id);
            if (_efforts.Levels is { } cached && _efforts.Model == id && cached.Count == (model?.Efforts.Count ?? 0) + 1) return cached;
            var levels = new List<KeyValuePair<string, string>> { new(ModelDefault, model?.DefaultEffort is { } d ? $"Model default ({IntelligenceModelOption.EffortName(d)})" : "Model default") };
            levels.AddRange((model?.Efforts ?? Array.Empty<string>()).Select(e => new KeyValuePair<string, string>(e, IntelligenceModelOption.EffortName(e))));
            _efforts = (id, levels);
            return levels;
        }
    }

    /// <summary>
    /// <see cref="DefaultEffort"/> by its place in <see cref="DefaultEfforts"/>,
    /// for the list in Settings. -1 is the list clearing itself and changes nothing.
    /// </summary>
    public int DefaultEffortIndex
    {
        get
        {
            var effort = DefaultEffort;
            var levels = DefaultEfforts;
            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].Key == effort) return i;
            }

            return 0;
        }
        set
        {
            var levels = DefaultEfforts;
            if (value < 0 || value >= levels.Count) return;
            DefaultEffort = levels[value].Key;
        }
    }

    public bool HasDefaultEfforts => DefaultEfforts.Count > 1;

    /// <summary>The list's key for "the model's own default" - a real key, since a list won't select an empty one.</summary>
    internal const string ModelDefault = "default";

    public string DefaultEffort
    {
        get => Chosen.Effort ?? ModelDefault;
        set
        {
            // Null is a list clearing its own selection, not a choice: the model's default is ModelDefault.
            if (string.IsNullOrEmpty(value)) return;
            var effort = value == ModelDefault ? null : value;
            if (Chosen.Effort == effort) return;
            Chosen.Effort = effort;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DefaultEffortIndex));
        }
    }

    private void Browse()
    {
        var filter = OperatingSystem.IsWindows()
            ? "Programs (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*"
            : "All files (*)|*";
        if (UiPlatform.FileDialogs.PickFileToOpen(new FileDialogOptions(filter, Title: $"Choose the {Provider.Name} executable")) is { } path)
        {
            ExecutablePath = path;
            CheckCommand.Execute(null);
        }
    }
}
