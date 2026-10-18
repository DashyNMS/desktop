using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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

    private ChatGptCredentials? _credentials;
    private CancellationTokenSource? _running;
    private string _question = string.Empty;
    private string? _errorText;
    private bool _isBusy;
    private bool _isSigningIn;
    private bool _consentGiven;
    private ChatGptModel? _selectedModel;
    private string? _conversationFor;

    public IntelligenceViewModel(
        IChatGptCredentialStore store,
        McpToolHost tools,
        ISessionService session,
        IWindowService windows,
        ILogger<IntelligenceViewModel> logger)
    {
        _store = store;
        _tools = tools;
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
        AcceptConsentCommand = new RelayCommand(() => ConsentGiven = true);
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

            SelectedModel = Models.FirstOrDefault(m => m.Slug == keep) ?? Models.FirstOrDefault();
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
            await Task.Run(() => _conversation.AskAsync(question, _selectedModel!.Slug, AccessTokenAsync, listener, _running.Token)).ConfigureAwait(true);
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
        _conversation.Clear();
        Messages.Clear();
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

        public void OnToolStarted(string name, string arguments) => _owner._dispatcher.InvokeAsync(() => _answer.AddActivity(IntelligenceMessageViewModel.DescribeTool(name)));

        public void OnToolFinished(string name, bool isError) => _owner._dispatcher.InvokeAsync(() => _answer.FinishActivity(IntelligenceMessageViewModel.DescribeTool(name), isError));
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
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>What was looked up for this answer: "Checking open alerts… done".</summary>
    public ObservableCollection<string> Activity { get; } = new();

    public void AddActivity(string description) => Activity.Add(description + "…");

    public void FinishActivity(string description, bool isError)
    {
        var index = Activity.IndexOf(description + "…");
        if (index >= 0)
        {
            Activity[index] = description + (isError ? " - failed" : " ✓");
        }
    }

    /// <summary>A tool's name as a person would say it.</summary>
    public static string DescribeTool(string name) => name switch
    {
        "active_alerts" => "Checking alerts",
        "alert_details" => "Reading an alert",
        "alert_rules" => "Reading alert rules",
        "config_backup" or "config_diff" or "recent_config_changes" => "Checking config backups",
        "device_details" => "Reading device details",
        "device_events" or "network_events" => "Reading the event log",
        "device_health" => "Checking device health",
        "device_neighbours" => "Finding neighbours",
        "device_sensors" or "health_overview" => "Reading sensors",
        "find_device" => "Finding the device",
        "list_groups_and_locations" => "Listing groups and locations",
        "network_overview" => "Getting the network overview",
        "port_status" => "Checking the port",
        "search_logs" => "Searching Graylog",
        "top_devices" or "top_interfaces" or "top_errors" => "Ranking devices and ports",
        "trace_endpoint" => "Tracing the address",
        "what_changed" => "Finding what changed",
        _ => "Looking it up",
    };
}
