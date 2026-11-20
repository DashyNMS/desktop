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
/// The model picker (#316) groups models by provider: a tab per provider that
/// can answer now - one that's signed out or not installed isn't offered here,
/// but in Settings - a search box, and the chosen model's reasoning levels beneath. Choosing a
/// model from another provider for a conversation already under way goes
/// through <see cref="_canSelect"/>, which asks first.
/// </remarks>
public sealed class IntelligenceComposerViewModel : ObservableObject
{
    private readonly Func<bool> _isLocked;
    private readonly Func<IntelligenceModelOption, bool> _canSelect;
    private readonly Func<string, IntelligenceModelOption?> _defaultModel;
    private readonly Func<string, string?> _defaultEffort;
    private string _question = string.Empty;
    private IntelligenceModelOption? _selectedModel;
    private string? _selectedEffort;
    private string _selectedDetail = IntelligenceDetail.Default;
    private bool _isBusy;
    private bool _canChangeChat;
    private IntelligenceProviderViewModel? _pickerProvider;
    private string _modelSearch = string.Empty;

    /// <param name="providers">Every provider; the picker offers only those that can answer.</param>
    /// <param name="isLocked">True while an answer runs, when the detail level can't change.</param>
    /// <param name="canSelect">Asked before a model from another provider replaces the current one; false keeps it.</param>
    /// <param name="defaultModel">The model a provider's tab starts with - Settings' choice, else its first.</param>
    /// <param name="defaultEffort">The reasoning level Settings chose for a provider, if any.</param>
    internal IntelligenceComposerViewModel(ObservableCollection<IntelligenceProviderViewModel> providers, AsyncRelayCommand send, RelayCommand stop, Func<bool> isLocked,
        Func<IntelligenceModelOption, bool>? canSelect = null, Func<string, IntelligenceModelOption?>? defaultModel = null, Func<string, string?>? defaultEffort = null)
    {
        _defaultModel = defaultModel ?? (id => providers.FirstOrDefault(p => p.Id == id)?.Models.FirstOrDefault());
        _defaultEffort = defaultEffort ?? (_ => null);
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
            if (p is not string effort || !CanChangeChat || ReasoningModel is not { } model) return;

            // A level on another provider's tab chooses that tab's model too.
            if (!Equals(model, SelectedModel))
            {
                SelectedModel = model;
                if (!Equals(model, SelectedModel)) return;
            }

            SelectedEffort = effort;
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

    /// <summary>
    /// The model the picker's Reasoning row is for: the chosen one when its
    /// provider's tab is open, otherwise the model that tab would start with -
    /// so a provider's levels show as soon as its tab is opened.
    /// </summary>
    public IntelligenceModelOption? ReasoningModel => PickerProvider is not { } provider ? _selectedModel
        : _selectedModel?.Provider == provider.Id ? _selectedModel
        : _defaultModel(provider.Id);

    /// <summary>The Reasoning row's levels, with the one that would be used marked.</summary>
    public IReadOnlyList<EffortOption> Efforts
    {
        get
        {
            var model = ReasoningModel;
            if (model is null) return new List<EffortOption>();
            var current = Equals(model, _selectedModel)
                ? EffectiveEffort
                : _defaultEffort(model.Provider) is { } chosen && model.Efforts.Contains(chosen) ? chosen : model.DefaultEffort;
            return model.Efforts.Select(e => new EffortOption(e, IntelligenceModelOption.EffortName(e), e == current)).ToList();
        }
    }

    /// <summary>The Reasoning row shows: the open tab's model has levels to choose.</summary>
    public bool HasEfforts => ReasoningModel?.HasEfforts == true;

    /// <summary>The level the next answer uses: the chosen one, or the model's default.</summary>
    public string? EffectiveEffort => _selectedEffort ?? _selectedModel?.DefaultEffort;

    /// <summary>The picker button: "Sonnet · High", or the model alone where there's no reasoning to choose.</summary>
    public string ModelButtonText => _selectedModel is null
        ? "Choose a model"
        : EffectiveEffort is { } effort && _selectedModel.HasEfforts ? $"{_selectedModel.Name} · {IntelligenceModelOption.EffortName(effort)}" : _selectedModel.Name;

    public string? SelectedProviderName => Providers.FirstOrDefault(p => p.Id == _selectedModel?.Provider)?.Name;

    public string? SelectedProviderId => _selectedModel?.Provider;

    /// <summary>The providers that can answer now - the picker's tabs.</summary>
    public IReadOnlyList<IntelligenceProviderViewModel> AvailableProviders => Providers.Where(p => p.IsReady).ToList();

    /// <summary>Tabs only when there's a choice to make.</summary>
    public bool HasProviderTabs => AvailableProviders.Count > 1;

    /// <summary>The provider tab open in the picker - always one that can answer.</summary>
    public IntelligenceProviderViewModel? PickerProvider
    {
        get => _pickerProvider is { IsReady: true } open ? open : AvailableProviders.FirstOrDefault();
        set
        {
            if (!SetProperty(ref _pickerProvider, value)) return;
            foreach (var provider in Providers) provider.IsPickerTab = ReferenceEquals(provider, value);
            RaisePicker();
        }
    }

    /// <summary>
    /// The picker is opening: it starts on the chat's own provider with the
    /// search cleared, however it was left last time.
    /// </summary>
    public void ResetPicker()
    {
        ModelSearch = string.Empty;
        PickerProvider = Providers.FirstOrDefault(p => p.Id == _selectedModel?.Provider && p.IsReady) ?? AvailableProviders.FirstOrDefault();
        RaisePicker();
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
        OnPropertyChanged(nameof(ReasoningModel));
        OnPropertyChanged(nameof(Efforts));
        OnPropertyChanged(nameof(HasEfforts));
        OnPropertyChanged(nameof(ModelButtonText));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedProviderId));
        OnPropertyChanged(nameof(PickerModels));
    }

    private void RaisePicker()
    {
        OnPropertyChanged(nameof(AvailableProviders));
        OnPropertyChanged(nameof(HasProviderTabs));
        OnPropertyChanged(nameof(PickerProvider));
        OnPropertyChanged(nameof(PickerModels));
        OnPropertyChanged(nameof(HasPickerModels));
        OnPropertyChanged(nameof(ReasoningModel));
        OnPropertyChanged(nameof(Efforts));
        OnPropertyChanged(nameof(HasEfforts));
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

    /// <summary>One reasoning level in the picker's segmented row: a short label to fit, the full name for screen readers.</summary>
    public sealed record EffortOption(string Key, string Name, bool IsSelected)
    {
        public string ShortName => Key == "xhigh" ? "X-high" : Name;
    }
}
