using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The Intelligence tab: ask about the network in plain words, answered by
/// the user's own ChatGPT plan using DashyNMS's read-only network tools.
/// </summary>
public sealed class IntelligenceViewModel : ObservableObject
{
    public static readonly Uri ManageUsageUrl = new("https://chatgpt.com/settings/usage");

    /// <summary>How long before expiry an access token is renewed.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);

    private readonly IChatGptCredentialStore _store;
    private readonly ChatGptSignIn _signIn;
    private readonly ResponsesClient _responses;
    private readonly McpToolHost _tools;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly ILogger<IntelligenceViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private readonly IntelligenceConversation _conversation;
    private readonly ISettingsStore _settings;
    private string? _defaultModel;

    private ChatGptCredentials? _credentials;
    private CancellationTokenSource? _running;
    private string _question = string.Empty;
    private string? _errorText;
    private bool _isBusy;
    private bool _isSigningIn;
    private bool _consentGiven;
    private DashyOutfit _companionOutfit = DashyOutfits.Pick(Random.Shared.Next(100));
    private ChatGptModel? _selectedModel;
    private string? _conversationFor;

    public IntelligenceViewModel(
        IChatGptCredentialStore store,
        McpToolHost tools,
        ISessionService session,
        IWindowService windows,
        IDeviceCache devices,
        ILibreNmsClient client,
        ISettingsStore settings,
        AlertMonitor alerts,
        ILogger<IntelligenceViewModel> logger)
    {
        _store = store;
        _tools = tools;
        Context = new IntelligenceContextViewModel(devices, client, settings, windows, alerts, logger);
        _settings = settings;
        _consentGiven = settings.Current.IntelligenceConsentGiven;

        // A new default model in Settings applies to the next conversation,
        // or straight away if this one hasn't started.
        // A new default model in Settings applies straight away. Only a change
        // to the default itself moves the picker, so a model picked for this
        // conversation stays put when other settings are saved.
        _defaultModel = settings.Current.IntelligenceModel;
        settings.Changed += (_, current) => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(ShowMascot));
            if (current.IntelligenceModel == _defaultModel)
            {
                return;
            }

            _defaultModel = current.IntelligenceModel;
            if (Models.FirstOrDefault(m => m.Slug == _defaultModel) is { } preferred)
            {
                SelectedModel = preferred;
            }
        });
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);
        _session = session;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _signIn = new ChatGptSignIn(http, "DashyNMS");
        _responses = new ResponsesClient(http);
        _conversation = new IntelligenceConversation(_responses, _tools);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_isSigningIn);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsSignedIn && !_isBusy);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        StopCommand = new RelayCommand(() => _running?.Cancel(), () => _isBusy);
        ClearCommand = new RelayCommand(Clear, () => !_isBusy && Messages.Count > 0);
        ManageUsageCommand = new RelayCommand(() => _windows.OpenUrl(ManageUsageUrl));
        AcceptConsentCommand = new RelayCommand(() =>
        {
            ConsentGiven = true;

            // Remembered, so the notice is shown once rather than every launch.
            _settings.Current.IntelligenceConsentGiven = true;
            _settings.Save();
        });
        OpenSettingsCommand = new RelayCommand(() => _windows.ShowSettingsSection(SettingsSection.Intelligence));
        AskSuggestionCommand = new AsyncRelayCommand(async p =>
        {
            Question = p as string ?? string.Empty;
            await SendAsync().ConfigureAwait(true);
        }, _ => CanSend || (IsSignedIn && !_isBusy));

        _credentials = _store.Load();
        if (IsSignedIn)
        {
            _ = LoadModelsAsync();
        }
    }

    public ObservableCollection<IntelligenceMessageViewModel> Messages { get; } = new();

    /// <summary>The context sidebar: Spotlight, mentioned devices, their alerts, what was looked at.</summary>
    public IntelligenceContextViewModel Context { get; }

    public RelayCommand ToggleSidebarCommand { get; }

    private bool _isSidebarOpen = true;

    /// <summary>Whether the person wants the sidebar - it still hides itself on a narrow window.</summary>
    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => SetProperty(ref _isSidebarOpen, value);
    }

    public ObservableCollection<ChatGptModel> Models { get; } = new();

    public string[] Suggestions { get; } =
    {
        "What's alerting right now?",
        "How is the network doing?",
        "Which ports are counting the most errors?",
        "What changed in the last 24 hours?",
    };

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand ManageUsageCommand { get; }

    public RelayCommand AcceptConsentCommand { get; }

    public AsyncRelayCommand AskSuggestionCommand { get; }

    public bool IsSignedIn => _credentials?.IsSignedIn == true;

    public bool IsSignedOut => !IsSignedIn;

    public bool CanUsePlan => _credentials?.CanUsePlan == true;

    public bool ToolsBundled => McpToolHost.IsBundled;

    /// <summary>The top bar's label - which plan answers, never whose account (that's in Settings).</summary>
    public string PlanText => IsSignedIn ? "Using your ChatGPT plan" : "Not connected";

    /// <summary>Settings › Intelligence: the account, only there.</summary>
    public string SignedInAsText => _credentials?.Email is { } email ? "Signed in as " + email : "Signed in";

    public RelayCommand OpenSettingsCommand { get; }

    public string NetworkText => _session.Connection?.WebRoot.Host is { } host ? $"Network: {host}" : "Not connected to LibreNMS";

    /// <summary>Shown once, before the first question: what leaves the computer, and where it goes.</summary>
    public bool ConsentGiven
    {
        get => _consentGiven;
        set
        {
            if (SetProperty(ref _consentGiven, value))
            {
                OnPropertyChanged(nameof(NeedsConsent));
                RaiseCommands();
            }
        }
    }

    public bool NeedsConsent => IsSignedIn && !_consentGiven;

    public bool ShowConversation => IsSignedIn && _consentGiven;

    public bool ShowSuggestions => ShowConversation && Messages.Count == 0;

    public ChatGptModel? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public string Question
    {
        get => _question;
        set
        {
            if (SetProperty(ref _question, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowMascot => _settings.Current.IntelligenceMascotEnabled;

    public DashyOutfit CompanionOutfit
    {
        get => _companionOutfit;
        private set => SetProperty(ref _companionOutfit, value);
    }

    public void OpenSource(Uri uri)
    {
        if (WebSearchCitations.TryGetLink(uri.AbsoluteUri, out var safeUri)) _windows.OpenUrl(safeUri);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    private bool CanSend => IsSignedIn && _consentGiven && !_isBusy && !string.IsNullOrWhiteSpace(_question) && _selectedModel is not null;

    private async Task ConnectAsync()
    {
        _isSigningIn = true;
        ErrorText = null;
        RaiseCommands();
        try
        {
            using var cancel = new CancellationTokenSource();
            var credentials = await _signIn.SignInAsync(_credentials ?? new ChatGptCredentials(), url => _windows.OpenUrl(url), cancel.Token).ConfigureAwait(true);

            // Another account's conversation never carries over.
            if (_credentials?.Subject is { } previous && previous != credentials.Subject)
            {
                _conversation.Clear();
                Messages.Clear();
            }

            _credentials = credentials;
            _store.Save(credentials);
            _logger.LogInformation("Intelligence: signed in with ChatGPT (plan usage {CanUse})", credentials.CanUsePlan);
            if (!credentials.CanUsePlan)
            {
                ErrorText = "You're signed in, but DashyNMS wasn't allowed to use your ChatGPT plan. Sign in again and allow it.";
            }

            RaiseAccount();
            await LoadModelsAsync().ConfigureAwait(true);
        }
        catch (ChatGptSignInException ex)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intelligence: ChatGPT sign-in failed");
            ErrorText = "Couldn't sign in with ChatGPT. Check your internet connection and try again.";
        }
        finally
        {
            _isSigningIn = false;
            RaiseCommands();
        }
    }

    /// <summary>
    /// Forgets the ChatGPT sign-in on this PC and stops the network tools.
    /// OpenAI doesn't document a revocation endpoint for this flow yet, so the
    /// tokens are deleted here; Manage usage on chatgpt.com lists connected apps.
    /// </summary>
    private async Task DisconnectAsync()
    {
        _running?.Cancel();
        _store.Clear();
        _credentials = null;
        _conversation.Clear();
        Messages.Clear();
        Models.Clear();
        SelectedModel = null;
        ErrorText = null;
        await _tools.StopAsync().ConfigureAwait(true);
        RaiseAccount();
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var token = await AccessTokenAsync(CancellationToken.None).ConfigureAwait(true);
            var models = await _responses.ListModelsAsync(token, CancellationToken.None).ConfigureAwait(true);
            var keep = _selectedModel?.Slug;
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(model);
            }

            SelectedModel = Models.FirstOrDefault(m => m.Slug == keep)
                            ?? Models.FirstOrDefault(m => m.Slug == _settings.Current.IntelligenceModel)
                            ?? Models.FirstOrDefault();
            if (Models.Count == 0)
            {
                ErrorText = "Your ChatGPT plan didn't list any models DashyNMS can use.";
            }
        }
        catch (Exception ex) when (ex is IntelligenceException or ChatGptSignInException or HttpRequestException)
        {
            _logger.LogWarning("Intelligence: couldn't list models: {Message}", ex.Message);
            ErrorText = ex.Message;
        }

        RaiseCommands();
    }

    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        // A different LibreNMS starts a new conversation - nothing from one
        // network is ever sent with another's.
        var network = _session.Connection?.WebRoot.ToString();
        if (network != _conversationFor)
        {
            if (_conversationFor is not null)
            {
                CompanionOutfit = DashyOutfits.Pick(Random.Shared.Next(100));
            }
            _conversation.Clear();
            Messages.Clear();
            _conversationFor = network;
        }

        var question = _question.Trim();
        Question = string.Empty;
        ErrorText = null;
        Messages.Add(IntelligenceMessageViewModel.User(question));
        var answer = IntelligenceMessageViewModel.Assistant();
        Messages.Add(answer);
        OnPropertyChanged(nameof(ShowSuggestions));

        _running = new CancellationTokenSource();
        IsBusy = true;
        var listener = new Listener(this, answer);
        try
        {
            var spotlight = Context.SpotlightForAgent;
            var model = _selectedModel!.Slug;
            var token = _running.Token;
            var webSearchEnabled = _settings.Current.IntelligenceWebSearchEnabled;
            await Task.Run(() => _conversation.AskAsync(question, model, AccessTokenAsync, listener, token, spotlight, webSearchEnabled)).ConfigureAwait(true);
            Context.OnAnswer(answer.Text);
            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                answer.Text = "(No answer.)";
            }
        }
        catch (OperationCanceledException)
        {
            answer.Text += string.IsNullOrEmpty(answer.Text) ? "Stopped." : "\n\nStopped.";
        }
        catch (IntelligenceException ex)
        {
            ErrorText = ex.Message;
            if (string.IsNullOrEmpty(answer.Text))
            {
                Messages.Remove(answer);
            }
        }
        catch (ChatGptSignInException ex)
        {
            ErrorText = ex.Message;
            Messages.Remove(answer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intelligence: the question failed");
            ErrorText = "Something went wrong answering that. Try again.";
            if (string.IsNullOrEmpty(answer.Text))
            {
                Messages.Remove(answer);
            }
        }
        finally
        {
            answer.IsStreaming = false;
            _running.Dispose();
            _running = null;
            IsBusy = false;
            OnPropertyChanged(nameof(ShowSuggestions));
        }
    }

    /// <summary>A current access token, renewed first if it's about to expire - one renewal at a time.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _renewing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = _credentials ?? throw new ChatGptSignInException("Sign in with ChatGPT first.");
            if (credentials.ExpiresAt - DateTimeOffset.UtcNow < RenewBefore)
            {
                try
                {
                    await _signIn.RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);
                    _store.Save(credentials);
                }
                catch (ChatGptSignInException)
                {
                    _store.Clear();
                    _credentials = null;
                    _ = _dispatcher.InvokeAsync(RaiseAccount);
                    throw;
                }
            }

            return credentials.AccessToken!;
        }
        finally
        {
            _renewing.Release();
        }
    }

    /// <summary>Intelligence turned off in Settings: stop anything running and the network tools.</summary>
    public async Task OnDisabledAsync()
    {
        _running?.Cancel();
        await _tools.StopAsync().ConfigureAwait(true);
    }

    private void Clear()
    {
        CompanionOutfit = DashyOutfits.Pick(Random.Shared.Next(100));
        _conversation.Clear();
        Context.Clear();
        Messages.Clear();

        // A new conversation starts on the default model.
        if (Models.FirstOrDefault(m => m.Slug == _defaultModel) is { } preferred)
        {
            SelectedModel = preferred;
        }

        ErrorText = null;
        OnPropertyChanged(nameof(ShowSuggestions));
        RaiseCommands();
    }

    private void RaiseAccount()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(CanUsePlan));
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(SignedInAsText));
        OnPropertyChanged(nameof(NeedsConsent));
        OnPropertyChanged(nameof(ShowConversation));
        OnPropertyChanged(nameof(ShowSuggestions));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(ShowConversation));
        OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(NetworkText));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        AskSuggestionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Streams the answer and tool activity into the panel, on the UI thread.</summary>
    private sealed class Listener : IIntelligenceListener
    {
        private readonly IntelligenceViewModel _owner;
        private readonly IntelligenceMessageViewModel _answer;

        public Listener(IntelligenceViewModel owner, IntelligenceMessageViewModel answer)
        {
            _owner = owner;
            _answer = answer;
        }

        public void OnText(string delta) => _owner._dispatcher.InvokeAsync(() => _answer.Text += delta);

        public void OnAnswerCompleted(string text) => _owner._dispatcher.InvokeAsync(() => _answer.Text = text);

        public void OnToolStarted(string name, string arguments) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.ToolStarted(name);
            _owner.Context.OnToolStarted(arguments);
        });

        public void OnToolFinished(string name, bool isError) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.ToolFinished(name, isError);
            _owner.Context.OnLookedAt(IntelligenceMessageViewModel.Subject(name));
        });

        public void OnImage(IntelligenceImage image) => _owner._dispatcher.InvokeAsync(() => _answer.AddImage(image));
    }
}

/// <summary>One message in the Intelligence conversation.</summary>
public sealed class IntelligenceMessageViewModel : ObservableObject
{
    private string _text = string.Empty;
    private bool _isStreaming;

    private IntelligenceMessageViewModel(bool isUser)
    {
        IsUser = isUser;
        _isStreaming = !isUser;
    }

    public static IntelligenceMessageViewModel User(string text) => new(isUser: true) { _text = text };

    public static IntelligenceMessageViewModel Assistant() => new(isUser: false);

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    public string Text
    {
        get => _text;
        set
        {
            var wasEmpty = _text.Length == 0;
            if (SetProperty(ref _text, value) && wasEmpty != (_text.Length == 0))
            {
                RaiseActivity();
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                RaiseActivity();
            }
        }
    }

    /// <summary>Looking something up, or waiting for the first words - drives the heartbeat.</summary>
    public bool IsWorking => IsAssistant && _isStreaming && (_currentStep is not null || _text.Length == 0);

    /// <summary>Graphs the answer drew on, shown above its text.</summary>
    public ObservableCollection<System.Windows.Media.ImageSource> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    public void AddImage(IntelligenceImage image)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(image.Data);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            Images.Add(bitmap);
            OnPropertyChanged(nameof(HasImages));
        }
        catch (Exception)
        {
            // Not an image WPF can read: the answer still says what it showed.
        }
    }

    // What was looked up for this answer, in the order first looked at.
    private readonly List<string> _lookedAt = new();
    private string? _currentStep;
    private int _failedLookups;

    /// <summary>
    /// One quiet line above the answer: the step in hand while it works
    /// ("Checking alerts…"), then what it looked at ("Looked at alerts and
    /// device health").
    /// </summary>
    public string? ActivityText => _currentStep is { } step
        ? step + "…"
        : _lookedAt.Count > 0
            ? "Looked at " + JoinNames(_lookedAt) + (_failedLookups > 0 ? $" ({_failedLookups} couldn't be read)" : string.Empty)
            : IsWorking ? "Thinking…" : null;

    public bool HasActivity => ActivityText is not null;

    public void ToolStarted(string tool)
    {
        _currentStep = Step(tool);
        RaiseActivity();
    }

    public void ToolFinished(string tool, bool isError)
    {
        var subject = Subject(tool);
        if (!_lookedAt.Contains(subject))
        {
            _lookedAt.Add(subject);
        }

        if (isError)
        {
            _failedLookups++;
        }

        _currentStep = null;
        RaiseActivity();
    }

    private void RaiseActivity()
    {
        OnPropertyChanged(nameof(ActivityText));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(IsWorking));
    }

    private static string JoinNames(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>What a tool is doing, as a person would say it.</summary>
    private static string Step(string tool) => tool switch
    {
        "web_search" => "Searching the web",
        "active_alerts" => "Checking alerts",
        "alert_details" => "Reading the alert",
        "alert_rules" => "Reading alert rules",
        "config_backup" or "config_diff" or "recent_config_changes" => "Checking config backups",
        "device_details" => "Reading device details",
        "device_events" or "network_events" => "Reading the event log",
        "device_health" => "Checking device health",
        "device_neighbours" => "Finding neighbours",
        "device_sensors" or "health_overview" => "Reading sensors",
        "find_device" => "Finding the device",
        "graph" => "Drawing the graph",
        "list_groups_and_locations" => "Listing groups and locations",
        "network_overview" => "Getting the network overview",
        "port_status" => "Checking the port",
        "search_logs" => "Searching Graylog",
        "top_devices" or "top_interfaces" or "top_errors" => "Ranking devices and ports",
        "trace_endpoint" => "Tracing the address",
        "what_changed" => "Finding what changed",
        _ => "Looking it up",
    };

    /// <summary>What a tool looked at, for "Looked at …".</summary>
    internal static string Subject(string tool) => tool switch
    {
        "web_search" => "the web",
        "active_alerts" or "alert_details" => "alerts",
        "alert_rules" => "alert rules",
        "config_backup" or "config_diff" or "recent_config_changes" => "config backups",
        "device_details" or "find_device" => "devices",
        "graph" => "graphs",
        "device_events" or "network_events" => "the event log",
        "device_health" => "device health",
        "device_neighbours" => "neighbours",
        "device_sensors" or "health_overview" => "sensors",
        "list_groups_and_locations" => "groups and locations",
        "network_overview" => "the network overview",
        "port_status" => "ports",
        "search_logs" => "Graylog",
        "top_devices" or "top_interfaces" or "top_errors" => "the busiest devices and ports",
        "trace_endpoint" => "where the address is plugged in",
        "what_changed" => "recent changes",
        _ => "the network",
    };
}
