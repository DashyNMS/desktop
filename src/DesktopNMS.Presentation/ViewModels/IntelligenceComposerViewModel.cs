using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Intelligence composer (#327): the question being written, the provider,
/// model, reasoning and detail level for this chat, and Ask and Stop. Asking
/// itself is the conversation's job; this holds what the next question goes out with.
/// </summary>
/// <remarks>
/// The model picker (#316) groups models by provider: a tab per provider, a
/// search box, and the chosen model's reasoning levels beneath. Choosing a
/// model from another provider for a conversation already under way goes
/// through <see cref="_canSelect"/>, which asks first.
/// </remarks>
public sealed class IntelligenceComposerViewModel : ObservableObject
{
    private readonly Func<bool> _isLocked;
    private readonly Func<IntelligenceModelOption, bool> _canSelect;
    private string _question = string.Empty;
    private IntelligenceModelOption? _selectedModel;
    private string? _selectedEffort;
    private string _selectedDetail = IntelligenceDetail.Default;
    private bool _isBusy;
    private bool _canChangeChat;
    private IntelligenceProviderViewModel? _pickerProvider;
    private string _modelSearch = string.Empty;

    /// <param name="providers">Every provider, ready or not - the picker explains the ones that aren't.</param>
    /// <param name="isLocked">True while an answer runs, when the detail level can't change.</param>
    /// <param name="canSelect">Asked before a model from another provider replaces the current one; false keeps it.</param>
    internal IntelligenceComposerViewModel(ObservableCollection<IntelligenceProviderViewModel> providers, AsyncRelayCommand send, RelayCommand stop, Func<bool> isLocked,
        Func<IntelligenceModelOption, bool>? canSelect = null)
    {
        Providers = providers;
        SendCommand = send;
        StopCommand = stop;
        _isLocked = isLocked;
        _canSelect = canSelect ?? (_ => true);
        SelectModelCommand = new RelayCommand(p =>
        {
            if (p is IntelligenceModelOption model && CanChangeChat) SelectedModel = model;
        }, p => p is IntelligenceModelOption && CanChangeChat);
        ShowProviderCommand = new RelayCommand(p =>
        {
            if (p is IntelligenceProviderViewModel provider) PickerProvider = provider;
        });
        SelectEffortCommand = new RelayCommand(p =>
        {
            if (p is string effort && CanChangeChat) SelectedEffort = effort;
        }, p => p is string && CanChangeChat);
        Providers.CollectionChanged += OnProvidersChanged;
        foreach (var provider in Providers) Watch(provider);
    }

    /// <summary>Something the chat saves changed: the draft, the model, reasoning or the detail level.</summary>
    public event EventHandler? Edited;

    public ObservableCollection<IntelligenceProviderViewModel> Providers { get; }

    public IReadOnlyList<KeyValuePair<string, string>> DetailOptions => IntelligenceDetailOptions.All;

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand SelectModelCommand { get; }

    public RelayCommand ShowProviderCommand { get; }

    public RelayCommand SelectEffortCommand { get; }

    /// <summary>Opens Settings › Intelligence from the picker's provider note.</summary>
    public RelayCommand? OpenProviderSettingsCommand { get; internal set; }

    public string Question
    {
        get => _question;
        set
        {
            if (SetProperty(ref _question, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
                Edited?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IntelligenceModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Equals(value, _selectedModel) && value?.Name == _selectedModel?.Name) return;
            if (value is not null && _selectedModel is not null && value.Provider != _selectedModel.Provider && !_canSelect(value))
            {
                OnPropertyChanged();
                return;
            }

            _selectedModel = value;
            OnPropertyChanged();
            if (value is not null && Providers.FirstOrDefault(p => p.Id == value.Provider) is { } provider) PickerProvider = provider;
            _selectedEffort = Normalise(_selectedEffort);
            RaiseModel();
            Edited?.Invoke(this, EventArgs.Empty);
            SendCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Restores a chat's model and reasoning together, without asking - it's already that chat's.</summary>
    internal void Restore(IntelligenceModelOption? model, string? effort)
    {
        _selectedModel = model;
        _selectedEffort = Normalise(effort);
        if (model is not null && Providers.FirstOrDefault(p => p.Id == model.Provider) is { } provider) PickerProvider = provider;
        OnPropertyChanged(nameof(SelectedModel));
        RaiseModel();
        SendCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The reasoning level, or null for the model's own default. Only levels the model offers are kept.</summary>
    public string? SelectedEffort
    {
        get => _selectedEffort;
        set
        {
            if (_isLocked()) { OnPropertyChanged(); return; }
            if (_selectedEffort == value) return;
            _selectedEffort = Normalise(value);
            RaiseModel();
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<EffortOption> Efforts => _selectedModel?.Efforts
        .Select(e => new EffortOption(e, IntelligenceModelOption.EffortName(e), e == EffectiveEffort)).ToList() ?? new List<EffortOption>();

    public bool HasEfforts => _selectedModel?.HasEfforts == true;

    /// <summary>The level the next answer uses: the chosen one, or the model's default.</summary>
    public string? EffectiveEffort => _selectedEffort ?? _selectedModel?.DefaultEffort;

    /// <summary>The picker button: "Sonnet · High", or the model alone where there's no reasoning to choose.</summary>
    public string ModelButtonText => _selectedModel is null
        ? "Choose a model"
        : EffectiveEffort is { } effort && HasEfforts ? $"{_selectedModel.Name} · {IntelligenceModelOption.EffortName(effort)}" : _selectedModel.Name;

    public string? SelectedProviderName => Providers.FirstOrDefault(p => p.Id == _selectedModel?.Provider)?.Name;

    public string? SelectedProviderId => _selectedModel?.Provider;

    /// <summary>The provider tab open in the picker.</summary>
    public IntelligenceProviderViewModel? PickerProvider
    {
        get => _pickerProvider ?? Providers.FirstOrDefault(p => p.IsReady) ?? Providers.FirstOrDefault();
        set
        {
            if (!SetProperty(ref _pickerProvider, value)) return;
            foreach (var provider in Providers) provider.IsPickerTab = ReferenceEquals(provider, value);
            RaisePicker();
        }
    }

    public string ModelSearch
    {
        get => _modelSearch;
        set
        {
            if (SetProperty(ref _modelSearch, value ?? string.Empty)) RaisePicker();
        }
    }

    /// <summary>The open tab's models matching the search - by name or id.</summary>
    public IReadOnlyList<IntelligenceModelOption> PickerModels
    {
        get
        {
            var models = (IEnumerable<IntelligenceModelOption>?)PickerProvider?.Models ?? Array.Empty<IntelligenceModelOption>();
            var search = _modelSearch.Trim();
            if (search.Length > 0)
            {
                models = models.Where(m => m.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                                           || m.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            return models.ToList();
        }
    }

    public bool HasPickerModels => PickerModels.Count > 0;

    /// <summary>
    /// The one line under the list: why the tab's provider can't answer, or
    /// what it can't do, or its usage. Shown before anyone relies on it.
    /// </summary>
    public string? PickerNote
    {
        get
        {
            if (PickerProvider is not { } provider) return null;
            if (!provider.IsReady) return provider.IsChecking ? $"Checking {provider.Name}…" : $"{provider.Name}: {provider.StatusText.ToLowerInvariant()}. Set it up in Settings.";
            if (!provider.Capabilities.WebSearch) return $"{provider.Name} answers from your network only: no web search." + (provider.UsageText is { } usage ? " " + usage + "." : string.Empty);
            if (provider.Models.Count == 0) return $"{provider.Name} didn't list any models.";
            return provider.UsageText;
        }
    }

    public bool HasPickerNote => !string.IsNullOrEmpty(PickerNote);

    /// <summary>How much this chat's next answer says (#329) - saved with the chat as its ReplyStyle.</summary>
    public string SelectedDetail
    {
        get => _selectedDetail;
        set
        {
            if (value is null) return;
            if (_isLocked()) { OnPropertyChanged(); return; }
            if (SetProperty(ref _selectedDetail, IntelligenceDetail.Normalize(value))) Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>An answer is running: Stop shows instead of Ask.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        internal set => SetProperty(ref _isBusy, value);
    }

    /// <summary>The model and detail level can change - not mid-answer, or without a chat.</summary>
    public bool CanChangeChat
    {
        get => _canChangeChat;
        internal set
        {
            if (!SetProperty(ref _canChangeChat, value)) return;
            SelectModelCommand.RaiseCanExecuteChanged();
            SelectEffortCommand.RaiseCanExecuteChanged();
        }
    }

    private string? Normalise(string? effort)
        => effort is not null && _selectedModel?.Efforts.Contains(effort) == true ? effort : null;

    private void RaiseModel()
    {
        OnPropertyChanged(nameof(SelectedEffort));
        OnPropertyChanged(nameof(EffectiveEffort));
        OnPropertyChanged(nameof(Efforts));
        OnPropertyChanged(nameof(HasEfforts));
        OnPropertyChanged(nameof(ModelButtonText));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(PickerModels));
    }

    private void RaisePicker()
    {
        OnPropertyChanged(nameof(PickerProvider));
        OnPropertyChanged(nameof(PickerModels));
        OnPropertyChanged(nameof(HasPickerModels));
        OnPropertyChanged(nameof(PickerNote));
        OnPropertyChanged(nameof(HasPickerNote));
    }

    private void OnProvidersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var provider in e.NewItems?.OfType<IntelligenceProviderViewModel>() ?? Enumerable.Empty<IntelligenceProviderViewModel>()) Watch(provider);
        RaisePicker();
    }

    private void Watch(IntelligenceProviderViewModel provider)
    {
        provider.Models.CollectionChanged += (_, _) => RaisePicker();
        provider.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IntelligenceProviderViewModel.Status) or nameof(IntelligenceProviderViewModel.Usage)) RaisePicker();
        };
    }

    /// <summary>One reasoning level in the picker's segmented row.</summary>
    public sealed record EffortOption(string Key, string Name, bool IsSelected);
}
