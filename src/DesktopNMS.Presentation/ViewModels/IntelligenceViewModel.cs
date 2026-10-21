using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
public sealed partial class IntelligenceViewModel : ObservableObject
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
    private readonly IUiDispatcher _dispatcher;
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

    public IntelligenceViewModel(
        IChatGptCredentialStore store,
        McpToolHost tools,
        ISessionService session,
        IWindowService windows,
        IDeviceCache devices,
        ILibreNmsClient client,
        ISettingsStore settings,
        AlertMonitor alerts,
        IIntelligenceChatStore chats,
        ILogger<IntelligenceViewModel> logger)
    {
        _store = store;
        _tools = tools;
        Context = new IntelligenceContextViewModel(devices, client, settings, windows, alerts, logger);
        _settings = settings;
        _consentGiven = IntelligenceConsent.IsCurrent(settings.Current);

        // A new default model in Settings applies to the next conversation,
        // or straight away if this one hasn't started.
        // A new default model in Settings applies straight away. Only a change
        // to the default itself moves the picker, so a model picked for this
        // conversation stays put when other settings are saved.
        _defaultModel = settings.Current.IntelligenceModel;
        settings.Changed += (_, current) => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(ShowMascot));
            OnPropertyChanged(nameof(ColourStatusIndicators));
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
        _dispatcher = UiThread.Dispatcher;

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _signIn = new ChatGptSignIn(http, "DashyNMS");
        _responses = new ResponsesClient(http);
        _conversation = new IntelligenceConversation(_responses, _tools, _logger);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_isSigningIn);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsSignedIn && !_isBusy);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        EditQuestionCommand = new RelayCommand(p => { if (p is IntelligenceMessageViewModel message) RestoreQuestion(message); }, CanRecoverQuestion);
        RetryQuestionCommand = new AsyncRelayCommand(async p =>
        {
            if (p is IntelligenceMessageViewModel message && RestoreQuestion(message)) await SendAsync().ConfigureAwait(true);
        }, p => CanRecoverQuestion(p) && SelectedModel is not null);
        StopCommand = new RelayCommand(() => _running?.Cancel(), () => _isBusy);
        ClearCommand = new RelayCommand(Clear, () => !_isBusy && ShowConversation);
        ManageUsageCommand = new RelayCommand(() => _windows.OpenUrl(ManageUsageUrl));
        AcceptConsentCommand = new RelayCommand(() =>
        {
            ConsentGiven = true;

            // Remembered, so the notice is shown once rather than every launch -
            // and again only when what it says changes (#323).
            IntelligenceConsent.Accept(_settings.Current);
            _settings.Save();
        });
        OpenSettingsCommand = new RelayCommand(() => _windows.ShowSettingsSection(SettingsSection.Intelligence));
        AskSuggestionCommand = new AsyncRelayCommand(async p =>
        {
            Question = p as string ?? string.Empty;
            await SendAsync().ConfigureAwait(true);
        }, _ => CanSend || (IsSignedIn && !_isBusy));

        _credentials = _store.Load();
        InitializeChats(chats);
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
    public RelayCommand EditQuestionCommand { get; }
    public AsyncRelayCommand RetryQuestionCommand { get; }
    public event EventHandler? QuestionFocusRequested;

    private bool CanRecoverQuestion(object? parameter) => CanManageChats && parameter is IntelligenceMessageViewModel message
        && Messages.Contains(message) && message.CanRecoverQuestion;

    private bool RestoreQuestion(IntelligenceMessageViewModel message)
    {
        if (!CanRecoverQuestion(message)) return false;
        if (!string.IsNullOrWhiteSpace(Question) && Question != message.SubmittedQuestion
            && !_windows.Confirm("Replace draft?", "Replace the unsent draft with this question?", "Replace draft")) return false;
        Question = message.SubmittedQuestion!;
        ErrorText = null;
        QuestionFocusRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

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
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                QueueChatSave();
                SendCommand?.RaiseCanExecuteChanged();
                RetryQuestionCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string Question
    {
        get => _question;
        set
        {
            if (SetProperty(ref _question, value ?? string.Empty))
            {
                SendCommand.RaiseCanExecuteChanged();
                QueueChatSave();
            }
        }
    }

    public bool ShowMascot => _settings.Current.IntelligenceMascotEnabled;

    public bool ColourStatusIndicators => _settings.Current.IntelligenceColourStatusIndicators;

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
                Context.IsScopeLocked = value;
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

            SaveActiveChat();

            _credentials = credentials;
            EnsureChatScope();
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
            _logger.LogWarning("Intelligence: ChatGPT sign-in failed ({ErrorType})", ex.GetType().Name);
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
        SaveActiveChat();
        _running?.Cancel();
        _store.Clear();
        _credentials = null;
        EnsureChatScope();
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
            var keep = _selectedChat?.Data.Model ?? _selectedModel?.Slug;
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
            _logger.LogWarning("Intelligence: couldn't list models ({ErrorType})", ex.GetType().Name);
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

        EnsureChatScope();

        var question = _question.Trim();
        Question = string.Empty;
        ErrorText = null;
        Messages.Add(IntelligenceMessageViewModel.User(question));
        var replyStyle = SelectedReplyStyle;
        var answer = IntelligenceMessageViewModel.Assistant(replyStyle, question);
        answer.SeedInlineReferences(Messages.Where(message => message.IsAssistant).Reverse().SelectMany(message => message.InlineReferences));
        Messages.Add(answer);
        if (_selectedChat is { } chat && !chat.Data.IsRenamed && chat.Data.Messages.Count == 0)
        {
            chat.Data.Title = ChatTitle(question);
            chat.Refresh();
        }
        SaveActiveChat(recordActivity: true);
        OnPropertyChanged(nameof(ShowSuggestions));

        _running = new CancellationTokenSource();
        IsBusy = true;
        var listener = new Listener(this, answer);
        try
        {
            var spotlight = Context.SpotlightForAgent;
            var model = _selectedModel!.Slug;
            var token = _running.Token;
            var webSearchEnabled = IntelligenceConsent.AllowsWebSearch(_settings.Current);
            await Task.Run(() => _conversation.AskAsync(question, model, AccessTokenAsync, listener, token, spotlight, webSearchEnabled, replyStyle)).ConfigureAwait(true);
            Context.OnAnswer(IntelligenceInlineMarkup.ToReadableText(answer.Text, answer.InlineReferences));
            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                answer.Finish(IntelligenceReplyOutcome.Failed, "No answer was returned. You can retry this question.");
            }
        }
        catch (OperationCanceledException)
        {
            answer.Finish(IntelligenceReplyOutcome.Stopped);
        }
        catch (IntelligenceException ex)
        {
            ErrorText = ex.Message;
            answer.Finish(IntelligenceReplyOutcome.Failed, ex.Message);
        }
        catch (ChatGptSignInException ex)
        {
            ErrorText = ex.Message;
            answer.Finish(IntelligenceReplyOutcome.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Intelligence: the question failed ({ErrorType})", ex.GetType().Name);
            ErrorText = "Something went wrong answering that. Try again.";
            answer.Finish(IntelligenceReplyOutcome.Failed, ErrorText);
        }
        finally
        {
            answer.IsStreaming = false;
            _running.Dispose();
            _running = null;
            IsBusy = false;
            SaveActiveChat(recordActivity: !string.IsNullOrWhiteSpace(answer.Text));
            EnsureChatScope();
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
        SaveActiveChat();
        await _tools.StopAsync().ConfigureAwait(true);
        SaveActiveChat();
    }

    private void Clear()
    {
        NewChat();

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
        EditQuestionCommand.RaiseCanExecuteChanged();
        RetryQuestionCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        AskSuggestionCommand.RaiseCanExecuteChanged();
        RaiseChatCommands();
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

        public void OnText(string delta) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.Text += delta;
            _owner.SaveWhileStreaming();
        });

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

        public void OnToolObserved(IntelligenceToolObservation observation) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.RecordToolObservation(observation);
            _owner.QueueChatSave();
        });

        public void OnStatusCard(IntelligenceStatusCard card) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.AddStatusCard(card);
            _owner.QueueChatSave();
        });
    }
}

/// <summary>One message in the Intelligence conversation.</summary>
public sealed class IntelligenceMessageViewModel : ObservableObject
{
    private string _text = string.Empty;
    private bool _isStreaming;
    private string? _restoredActivity;
    private bool _isCopied;
    private IUiTimer? _copyFeedbackTimer;
    private bool _isActivityExpanded;
    private readonly HashSet<string> _observedToolsAwaitingFinish = new();
    private readonly List<IntelligenceImage> _savedImages = new();

    private IntelligenceMessageViewModel(bool isUser)
    {
        IsUser = isUser;
        _isStreaming = !isUser;
        Outcome = isUser ? IntelligenceReplyOutcome.Completed : IntelligenceReplyOutcome.Generating;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public static IntelligenceMessageViewModel User(string text) => new(isUser: true) { _text = text };

    public static IntelligenceMessageViewModel Assistant(string? replyStyle = null, string? question = null) => new(isUser: false) { ReplyStyle = replyStyle, SubmittedQuestion = question };

    /// <summary>The presentation chosen for this reply, kept stable when settings change later.</summary>
    public string? ReplyStyle { get; private init; }

    public bool IsConversational => ReplyStyle == IntelligenceReplyStyles.Conversational;

    public DateTimeOffset? CreatedAt { get; private init; }
    public string TimeLabel => CreatedAt?.ToLocalTime().ToString("HH:mm") ?? string.Empty;
    public string TimeTooltip => CreatedAt?.ToLocalTime().ToString("f") ?? string.Empty;
    public bool CanCopyAnswer => IsAssistant && !string.IsNullOrWhiteSpace(Text);
    public string CopyMarkdown => IntelligenceInlineMarkup.ToReadableMarkdown(Text, InlineReferences).Trim();
    public string CopyAnswerLabel => _isCopied ? "Copied" : "Copy answer";
    public IntelligenceReplyOutcome Outcome { get; private set; } = IntelligenceReplyOutcome.Generating;
    public string? SubmittedQuestion { get; private init; }
    public string? FailureText { get; private set; }
    public bool HasFailureText => !string.IsNullOrEmpty(FailureText);
    public bool CanRecoverQuestion => IsAssistant && !IsStreaming
        && (Outcome is IntelligenceReplyOutcome.Failed or IntelligenceReplyOutcome.Stopped or IntelligenceReplyOutcome.Interrupted)
        && !string.IsNullOrWhiteSpace(SubmittedQuestion);
    public string OutcomeLabel => Outcome switch
    {
        IntelligenceReplyOutcome.Generating => "Answer in progress",
        IntelligenceReplyOutcome.Stopped => "Stopped",
        IntelligenceReplyOutcome.Interrupted => "Interrupted",
        IntelligenceReplyOutcome.Failed => "Failed",
        _ => _failedLookups > 0 ? "Completed · some lookups failed" : "Completed",
    };
    public string CompletionGlyph => Outcome == IntelligenceReplyOutcome.Stopped ? "■"
        : _failedLookups > 0 || Outcome is IntelligenceReplyOutcome.Failed or IntelligenceReplyOutcome.Interrupted ? "!" : "✓";
    public void Finish(IntelligenceReplyOutcome outcome, string? failureText = null)
    {
        Outcome = outcome;
        FailureText = failureText;
        _currentStep = null;
        IsStreaming = false;
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(HasFailureText));
        OnPropertyChanged(nameof(CanRecoverQuestion));
        RaiseActivity();
    }
    public bool IsActivityExpanded { get => _isActivityExpanded; set => SetProperty(ref _isActivityExpanded, value); }
    public ObservableCollection<IntelligenceToolObservationViewModel> ToolObservations { get; } = new();
    public bool HasToolObservations => ToolObservations.Count > 0;

    public void MarkCopied()
    {
        _isCopied = true;
        OnPropertyChanged(nameof(CopyAnswerLabel));
        if (_copyFeedbackTimer is null)
        {
            _copyFeedbackTimer = UiThread.CreateTimer(TimeSpan.FromSeconds(2));
            _copyFeedbackTimer.Tick += (_, _) =>
            {
                _copyFeedbackTimer.Stop();
                _isCopied = false;
                OnPropertyChanged(nameof(CopyAnswerLabel));
            };
        }
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    public void RecordToolObservation(IntelligenceToolObservation observation)
    {
        if (ToolObservations.Count < 48) ToolObservations.Add(new(observation));
        _observedToolsAwaitingFinish.Add(observation.Tool);
        OnPropertyChanged(nameof(HasToolObservations));
    }

    public SavedIntelligenceMessage Save() => new()
    {
        IsUser = IsUser,
        CreatedAt = CreatedAt,
        Text = Text,
        Activity = ActivityText,
        ReplyStyle = ReplyStyle,
        StatusCards = _statusSnapshots.ToList(),
        PriorReferences = _priorReferences.Values.Where(reference => Text.Contains("[[" + reference.Key + "]]", StringComparison.Ordinal)).ToList(),
        ToolObservations = ToolObservations.Select(item => item.Data).ToList(),
        IsIncomplete = IsStreaming,
        Outcome = Outcome,
        SubmittedQuestion = SubmittedQuestion,
        FailureText = FailureText,
        Images = _savedImages.ToList(),
    };

    public static IntelligenceMessageViewModel Restore(SavedIntelligenceMessage saved)
    {
        var message = new IntelligenceMessageViewModel(saved.IsUser)
        {
            _text = saved.Text + (saved.IsIncomplete && !saved.IsUser ? "\n\n(Interrupted when DashyNMS closed.)" : string.Empty),
            _isStreaming = false,
            _restoredActivity = saved.IsIncomplete ? "Interrupted" : saved.Activity,
            ReplyStyle = saved.ReplyStyle,
            CreatedAt = saved.CreatedAt,
            Outcome = saved.IsIncomplete || saved.Outcome == IntelligenceReplyOutcome.Generating ? IntelligenceReplyOutcome.Interrupted : saved.Outcome ?? IntelligenceReplyOutcome.Completed,
            SubmittedQuestion = saved.SubmittedQuestion,
            FailureText = saved.FailureText,
        };
        foreach (var image in saved.Images) message.AddImage(image);
        message.SeedInlineReferences(saved.PriorReferences);
        foreach (var observation in saved.ToolObservations) message.ToolObservations.Add(new(observation));
        message._failedLookups = saved.ToolObservations.Count(item => item.IsError);
        foreach (var card in saved.StatusCards) message.AddStatusCard(card);
        return message;
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    private readonly List<IntelligenceStatusCard> _statusSnapshots = new();
    private readonly Dictionary<string, IntelligenceInlineReference> _inlineReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IntelligenceInlineReference> _priorReferences = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IntelligenceInlineReference> InlineReferences => _inlineReferences.Values
        .Concat(_priorReferences.Values.Where(reference => !_inlineReferences.ContainsKey(reference.Key))).ToArray();

    public void SeedInlineReferences(IEnumerable<IntelligenceInlineReference> references)
    {
        foreach (var reference in references)
        {
            if (_priorReferences.Count >= 2000) break;
            // A previous lookup may resolve the name, but cannot claim a current operational state.
            _priorReferences.TryAdd(reference.Key, reference with { Status = "", Tone = "unknown", Detail = "Name verified earlier in this chat; current status not supplied by this reply" });
        }
        OnPropertyChanged(nameof(InlineReferences));
    }

    public void AddStatusCard(IntelligenceStatusCard card)
    {
        // Accumulate pages. The first observed status for an object in this reply
        // stays fixed rather than changing when a later page arrives.
        if (_statusSnapshots.Count >= 48) return;
        _statusSnapshots.Add(card);
        foreach (var reference in IntelligenceInlineReferences.FromSnapshot(card))
        {
            if (_inlineReferences.Count >= 2000) break;
            _inlineReferences.TryAdd(reference.Key, reference);
        }
        OnPropertyChanged(nameof(InlineReferences));
    }

    public string Text
    {
        get => _text;
        set
        {
            var wasEmpty = _text.Length == 0;
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(CanCopyAnswer));
                if (wasEmpty != (_text.Length == 0)) RaiseActivity();
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
                if (!value && Outcome == IntelligenceReplyOutcome.Generating) Outcome = IntelligenceReplyOutcome.Completed;
                OnPropertyChanged(nameof(CanRecoverQuestion));
                RaiseActivity();
            }
        }
    }

    /// <summary>Looking something up, or waiting for the first words - drives the heartbeat.</summary>
    public bool IsWorking => IsAssistant && _isStreaming;

    /// <summary>Graphs the answer drew on, shown above its text.</summary>
    /// <remarks>The image bytes; the view decodes them, and leaves out any it can't read (#249).</remarks>
    public ObservableCollection<byte[]> Images { get; } = new();

    public bool HasImages => Images.Count > 0;

    public void AddImage(IntelligenceImage image)
    {
        Images.Add(image.Data);
        _savedImages.Add(image);
        OnPropertyChanged(nameof(HasImages));
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
    public string? ActivityText => Outcome is IntelligenceReplyOutcome.Stopped or IntelligenceReplyOutcome.Interrupted or IntelligenceReplyOutcome.Failed ? OutcomeLabel
        : _currentStep is { } step
        ? step + "…"
        : _lookedAt.Count > 0
            ? "Looked at " + JoinNames(_lookedAt) + (_failedLookups > 0 ? $" ({_failedLookups} couldn't be read)" : string.Empty)
            : IsWorking ? "Thinking…" : _restoredActivity;

    public bool HasActivity => ActivityText is not null;

    public void ToolStarted(string tool)
    {
        _currentStep = Step(tool);
        RaiseActivity();
    }

    public void ToolFinished(string tool, bool isError)
    {
        if (!_observedToolsAwaitingFinish.Remove(tool))
        {
            // Hosted web search reports activity events rather than a local tool payload.
            RecordToolObservation(new(tool, DateTimeOffset.UtcNow, isError ? "Lookup failed." : "Lookup completed.", isError));
            _observedToolsAwaitingFinish.Remove(tool);
        }
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
        OnPropertyChanged(nameof(CompletionGlyph));
        OnPropertyChanged(nameof(OutcomeLabel));
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
