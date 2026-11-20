using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services.Intelligence.Agents;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Settings › Providers (#316): the providers DashyNMS lists, each with its
/// switch for the model picker, and the chosen one's details - how it
/// connects, its account, usage, which models the picker shows and what new
/// chats start with. Everything here is part of Settings' draft and saved with it.
/// </summary>
/// <remarks>
/// ChatGPT is one entry with two connections - DashyNMS's own sign-in or Codex
/// on this computer - and is always listed. Another provider is listed once
/// it's added, or as soon as it's found installed; Add provider offers the rest.
/// </remarks>
public sealed class IntelligenceProvidersViewModel : ObservableObject
{
    private readonly AppSettings _draft;
    private readonly IntelligenceViewModel _intelligence;
    private IntelligenceProviderEntryViewModel? _selected;
    private bool _isAdding;

    internal IntelligenceProvidersViewModel(IntelligenceViewModel intelligence, AppSettings draft)
    {
        _intelligence = intelligence;
        _draft = draft;
        var chatGpt = new IntelligenceProviderEntryViewModel(this, AppSettings.IntelligenceProviderChatGpt, "ChatGPT", "Plus or Pro: sign in, or use Codex");
        All = [chatGpt];
        foreach (var provider in intelligence.Providers.Where(p => p.IsAgent && p.Id != AppSettings.IntelligenceProviderCodex))
        {
            All.Add(new IntelligenceProviderEntryViewModel(this, provider.Id, provider.Name, Requirement(provider.Id)));
        }

        foreach (var provider in intelligence.Providers)
        {
            ((INotifyPropertyChanged)provider).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IntelligenceProviderViewModel.Status)) Refresh();
            };
        }

        AddCommand = new RelayCommand(() => IsAdding = true);
        CancelAddCommand = new RelayCommand(() => IsAdding = false);
        AddProviderCommand = new RelayCommand(p =>
        {
            // One of each: a provider already in the list can't be added again.
            if (p is not IntelligenceProviderEntryViewModel entry || Listed.Contains(entry)) return;
            Chosen(entry.Id).Added = true;
            IsAdding = false;
            Refresh();
            Selected = entry;
            _ = entry.Route?.CheckAsync(System.Threading.CancellationToken.None);
        });
        Refresh();
        Selected = Listed.FirstOrDefault();
    }

    /// <summary>Every provider DashyNMS supports, listed or not - Add provider shows them all, the listed ones unavailable.</summary>
    public List<IntelligenceProviderEntryViewModel> All { get; }

    /// <summary>The providers shown in the list.</summary>
    public ObservableCollection<IntelligenceProviderEntryViewModel> Listed { get; } = new();

    /// <summary>The providers Add provider offers: supported, not listed yet.</summary>
    public IReadOnlyList<IntelligenceProviderEntryViewModel> Addable => All.Where(e => !Listed.Contains(e)).ToList();

    public bool HasAddable => Addable.Count > 0;

    public RelayCommand AddCommand { get; }

    public RelayCommand CancelAddCommand { get; }

    public RelayCommand AddProviderCommand { get; }

    /// <summary>The Add provider panel is open.</summary>
    public bool IsAdding
    {
        get => _isAdding;
        set => SetProperty(ref _isAdding, value);
    }

    public IntelligenceProviderEntryViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            foreach (var entry in Listed) entry.IsSelected = ReferenceEquals(entry, value);
        }
    }

    /// <summary>The provider a new conversation starts with, from the listed ones.</summary>
    public string DefaultProvider
    {
        get => _draft.IntelligenceProvider;
        set
        {
            if (value is null || _draft.IntelligenceProvider == value) return;
            _draft.IntelligenceProvider = value;
            OnPropertyChanged();
        }
    }

    internal IntelligenceViewModel Intelligence => _intelligence;

    internal AppSettings Draft => _draft;

    /// <summary>A provider's draft settings, created when first needed.</summary>
    internal IntelligenceProviderSettings Chosen(string id)
    {
        if (!_draft.IntelligenceProviders.TryGetValue(id, out var chosen))
        {
            chosen = new IntelligenceProviderSettings();
            _draft.IntelligenceProviders[id] = chosen;
        }

        return chosen;
    }

    /// <summary>Lists ChatGPT, then every provider added or found installed, in a fixed order.</summary>
    internal void Refresh()
    {
        var listed = All.Where(e => e.Id == AppSettings.IntelligenceProviderChatGpt
                                    || _draft.IntelligenceProviders.TryGetValue(e.Id, out var chosen) && chosen.Added
                                    || e.Route?.State is AgentProviderState.Ready or AgentProviderState.SignedOut or AgentProviderState.Unavailable).ToList();
        for (var i = Listed.Count - 1; i >= 0; i--)
        {
            if (!listed.Contains(Listed[i])) Listed.RemoveAt(i);
        }

        foreach (var entry in listed.Where(e => !Listed.Contains(e))) Listed.Insert(Math.Min(listed.IndexOf(entry), Listed.Count), entry);
        foreach (var entry in All) entry.Raise();
        OnPropertyChanged(nameof(Addable));
        OnPropertyChanged(nameof(HasAddable));
        AddCommand.RaiseCanExecuteChanged();
        if (Selected is null || !Listed.Contains(Selected)) Selected = Listed.FirstOrDefault();
    }

    /// <summary>Takes a provider off the list - ChatGPT always stays. Its sign-in and chats are kept.</summary>
    internal void Remove(IntelligenceProviderEntryViewModel entry)
    {
        if (entry.Id == AppSettings.IntelligenceProviderChatGpt) return;
        var chosen = Chosen(entry.Id);
        chosen.Added = false;
        chosen.Enabled = false;
        Refresh();
    }

    private static string Requirement(string id) => id switch
    {
        AppSettings.IntelligenceProviderClaudeCode => "A Claude plan or Anthropic API key",
        AppSettings.IntelligenceProviderCopilot => "Any Copilot plan, Free included",
        _ => "Installed on this computer",
    };
}

/// <summary>One provider in Settings › Providers: its line in the list and its details.</summary>
public sealed class IntelligenceProviderEntryViewModel : ObservableObject
{
    private readonly IntelligenceProvidersViewModel _owner;
    private bool _isSelected;

    internal IntelligenceProviderEntryViewModel(IntelligenceProvidersViewModel owner, string id, string name, string requirement)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Requirement = requirement;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this), () => !IsChatGpt);
        UseSignInCommand = new RelayCommand(() => Connection = AppSettings.IntelligenceConnectionSignIn);
        UseCodexCommand = new RelayCommand(() => Connection = AppSettings.IntelligenceConnectionCodex);
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>What it needs before it can answer - for Add provider.</summary>
    public string Requirement { get; }

    public bool IsChatGpt => Id == AppSettings.IntelligenceProviderChatGpt;

    /// <summary>In the provider list already - Add provider shows it as added and won't add it twice.</summary>
    public bool IsListed => _owner.Listed.Contains(this);

    public bool CanAdd => !IsListed;

    /// <summary>
    /// Add provider's second line: how to install it when it isn't, how to
    /// sign in when it's installed but signed out, otherwise what it needs.
    /// </summary>
    public string AddNote => Route switch
    {
        { State: AgentProviderState.NotInstalled, SetupCommandText: { } install } => "Not installed. Install with " + install,
        { State: AgentProviderState.SignedOut, SetupCommandText: { } signIn } => "Installed. Sign in with " + signIn,
        _ => Requirement,
    };

    public RelayCommand RemoveCommand { get; }

    public RelayCommand UseSignInCommand { get; }

    public RelayCommand UseCodexCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>For ChatGPT: <see cref="AppSettings.IntelligenceConnectionSignIn"/> or <see cref="AppSettings.IntelligenceConnectionCodex"/>.</summary>
    public string Connection
    {
        get => _owner.Draft.IntelligenceProviders.TryGetValue(Id, out var chosen) && chosen.Connection is { } connection
            ? connection
            : _owner.Intelligence.ChatGptConnection;
        set
        {
            if (!IsChatGpt || value == Connection) return;
            _owner.Chosen(Id).Connection = value;
            Raise();
        }
    }

    public bool UsesSignIn => IsChatGpt && Connection == AppSettings.IntelligenceConnectionSignIn;

    public bool UsesCodex => IsChatGpt && Connection == AppSettings.IntelligenceConnectionCodex;

    /// <summary>The provider that answers for this entry: ChatGPT's chosen route, or the agent itself.</summary>
    public IntelligenceProviderViewModel? Route
    {
        get
        {
            var id = IsChatGpt && Connection == AppSettings.IntelligenceConnectionCodex ? AppSettings.IntelligenceProviderCodex : Id;
            return _owner.Intelligence.Providers.FirstOrDefault(p => p.Id == id);
        }
    }

    /// <summary>The route's executable and defaults, when it's an agent on this computer.</summary>
    public IntelligenceAgentSettingsViewModel? Agent => Route is { IsAgent: true } route ? AgentFor(route) : null;

    public bool IsAgentRoute => Agent is not null;

    /// <summary>Offered in the model picker - the list's switch.</summary>
    public bool Enabled
    {
        get => !_owner.Draft.IntelligenceProviders.TryGetValue(Id, out var chosen) || chosen.Enabled;
        set
        {
            if (value == Enabled) return;
            _owner.Chosen(Id).Enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The list's second line: ready and on what plan, or what's wrong.</summary>
    public string StatusLine => Route switch
    {
        null => "Not available",
        { IsReady: true } route => "Ready" + (Plan(route.AccountText) is { } plan ? " · " + plan : string.Empty),
        { } route => route.StatusText,
    };

    public bool IsReady => Route?.IsReady == true;

    /// <summary>The list's version beside the name: the agent's, or the route for ChatGPT through Codex.</summary>
    public string? VersionText => Route?.Status.Version is { } version ? (UsesCodex ? "Codex " : string.Empty) + Shorten(version) : null;

    /// <summary>The icon the list shows - OpenAI's for ChatGPT, whichever way it connects.</summary>
    public string IconId => Id;

    /// <summary>The route's models, each with its picker switch.</summary>
    public IReadOnlyList<ModelSwitch> Models => Route?.Models.Select(m => new ModelSwitch(this, m)).ToList() ?? new List<ModelSwitch>();

    public bool HasModels => Route?.Models.Count > 0;

    public string ModelsSummary
    {
        get
        {
            var hidden = Hidden;
            var total = Route?.Models.Count ?? 0;
            var shown = Route?.Models.Count(m => !hidden.Contains(m.Id)) ?? 0;
            return shown == total ? $"All {total} shown in the picker" : $"{shown} of {total} shown in the picker";
        }
    }

    private List<string> Hidden => Route is { } route && _owner.Draft.IntelligenceProviders.TryGetValue(route.Id, out var chosen) ? chosen.HiddenModels : new List<string>();

    internal void SetShown(IntelligenceModelOption model, bool shown)
    {
        if (Route is not { } route) return;
        var hidden = _owner.Chosen(route.Id).HiddenModels;
        if (shown) hidden.Remove(model.Id);
        else if (!hidden.Contains(model.Id)) hidden.Add(model.Id);
        OnPropertyChanged(nameof(ModelsSummary));
    }

    internal bool IsShown(IntelligenceModelOption model) => !Hidden.Contains(model.Id);

    private readonly Dictionary<string, IntelligenceAgentSettingsViewModel> _agents = new(StringComparer.Ordinal);

    private IntelligenceAgentSettingsViewModel AgentFor(IntelligenceProviderViewModel route)
    {
        if (!_agents.TryGetValue(route.Id, out var agent))
        {
            agent = new IntelligenceAgentSettingsViewModel(route, _owner.Draft);
            _agents[route.Id] = agent;
        }

        return agent;
    }

    internal void Raise()
    {
        foreach (var name in new[]
                 {
                     nameof(Connection), nameof(UsesSignIn), nameof(UsesCodex), nameof(Route), nameof(Agent), nameof(IsAgentRoute),
                     nameof(StatusLine), nameof(IsReady), nameof(VersionText), nameof(Models), nameof(HasModels), nameof(ModelsSummary), nameof(Enabled),
                     nameof(IsListed), nameof(CanAdd), nameof(AddNote),
                 })
        {
            OnPropertyChanged(name);
        }

        Agent?.RaiseDefaults();
    }

    /// <summary>"Signed in as … (ChatGPT Plus)" → "ChatGPT Plus".</summary>
    private static string? Plan(string? account)
    {
        if (account is null) return null;
        var open = account.LastIndexOf('(');
        return open >= 0 && account.EndsWith(')') ? account[(open + 1)..^1] : null;
    }

    /// <summary>"0.167.0-alpha.3.2" → "0.167": enough to tell versions apart in a narrow list.</summary>
    private static string Shorten(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        return parts.Length >= 3 && parts[2] == "0" ? string.Join(".", parts.Take(2)) : string.Join(".", parts);
    }

    /// <summary>One model's switch for whether the picker shows it.</summary>
    public sealed class ModelSwitch(IntelligenceProviderEntryViewModel owner, IntelligenceModelOption model) : ObservableObject
    {
        public IntelligenceModelOption Model { get; } = model;

        public string Name => Model.Name;

        public bool IsShown
        {
            get => owner.IsShown(Model);
            set
            {
                owner.SetShown(Model, value);
                OnPropertyChanged();
            }
        }
    }
}
