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
/// <remarks>
/// This holds the account, the conversation and asking. The chat list
/// (<see cref="Chats"/>), the composer (<see cref="Composer"/>) and the context
/// sidebar (<see cref="Context"/>) are their own view models (#327).
/// </remarks>
public sealed partial class IntelligenceViewModel : ObservableObject, IDisposable
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
    private readonly IDeviceCache _devices;
    private readonly IntelligenceStarterQuestions _starterQuestions = new(Random.Shared.Next(12));

    private ChatGptCredentials? _credentials;
    private CancellationTokenSource? _running;
    private string? _errorText;
    private bool _isBusy;
    private bool _isSigningIn;
    private bool _consentGiven;
    private DashyOutfit _companionOutfit = DashyOutfits.Pick(Random.Shared.Next(100));

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
        _devices = devices;
        _tools = tools;
        Context = new IntelligenceContextViewModel(devices, client, settings, windows, alerts, logger);
        _settings = settings;
        _consentGiven = IntelligenceConsent.IsCurrent(settings.Current);
        _session = session;
        _windows = windows;
        _logger = logger;
        _dispatcher = UiThread.Dispatcher;

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
                Composer!.SelectedModel = preferred;
            }
        });
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarOpen = !IsSidebarOpen);

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _signIn = new ChatGptSignIn(http, "DashyNMS");
        _responses = new ResponsesClient(http);
        _conversation = new IntelligenceConversation(_responses, _tools, _logger);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_isSigningIn);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsSignedIn && !_isBusy);
        var send = new AsyncRelayCommand(SendAsync, () => CanSend);
        var stop = new RelayCommand(() => _running?.Cancel(), () => _isBusy);
        Composer = new IntelligenceComposerViewModel(Models, send, stop, () => IsBusy && !Chats!.IsChangingChat);
        Composer.Edited += (_, _) => Chats!.QueueSave();
        Composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IntelligenceComposerViewModel.SelectedModel))
            {
                RetryQuestionCommand!.RaiseCanExecuteChanged();
                AskSuggestionCommand?.RaiseCanExecuteChanged();
            }
            if (e.PropertyName == nameof(IntelligenceComposerViewModel.Question))
            {
                OnPropertyChanged(nameof(ShowSuggestions));
                AskSuggestionCommand?.RaiseCanExecuteChanged();
            }
        };
        EditQuestionCommand = new RelayCommand(p => { if (p is IntelligenceMessageViewModel message) RestoreQuestion(message); }, CanRecoverQuestion);
        RetryQuestionCommand = new AsyncRelayCommand(async p =>
        {
            if (p is IntelligenceMessageViewModel message && RestoreQuestion(message)) await SendAsync().ConfigureAwait(true);
        }, p => CanRecoverQuestion(p) && Composer.SelectedModel is not null);
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
            if (!ShowSuggestions || p is not string question || !Suggestions.Contains(question)) return;
            Composer.Question = question;
            await SendAsync().ConfigureAwait(true);
        }, p => ShowSuggestions && !_isBusy && Composer.SelectedModel is not null && p is string question && Suggestions.Contains(question));

        _credentials = _store.Load();
        Chats = new IntelligenceChatListViewModel(this, chats, windows, _dispatcher, logger) { NewChatCommand = ClearCommand };
        Context.Spotlight.CollectionChanged += (_, _) => Chats.QueueSave();
        _session.StateChanged += (_, _) => _dispatcher.InvokeAsync(Chats.EnsureScope);
        Chats.EnsureScope();
        if (IsSignedIn)
        {
            _ = LoadModelsAsync();
        }
    }

    public ObservableCollection<IntelligenceMessageViewModel> Messages { get; } = new();

    /// <summary>The saved chats for this account and network.</summary>
    public IntelligenceChatListViewModel Chats { get; }

    /// <summary>The question box, model and detail level, and Ask and Stop.</summary>
    public IntelligenceComposerViewModel Composer { get; }

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

    /// <summary>The models the plan offers - the composer's picker and Settings' default.</summary>
    public ObservableCollection<ChatGptModel> Models { get; } = new();

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    private void RotateSuggestions()
    {
        if (Messages.Count != 0 || !string.IsNullOrWhiteSpace(Composer.Question)) return;
        Suggestions = _starterQuestions.Next(_devices.All.Count > 0, Context.HasOpenNetworkAlerts);
        OnPropertyChanged(nameof(Suggestions));
    }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public RelayCommand EditQuestionCommand { get; }
    public AsyncRelayCommand RetryQuestionCommand { get; }
    public event EventHandler? QuestionFocusRequested;

    private bool CanRecoverQuestion(object? parameter) => Chats.CanManageChats && parameter is IntelligenceMessageViewModel message
        && Messages.Contains(message) && message.CanRecoverQuestion;

    private bool RestoreQuestion(IntelligenceMessageViewModel message)
    {
        if (!CanRecoverQuestion(message)) return false;
        if (!string.IsNullOrWhiteSpace(Composer.Question) && Composer.Question != message.SubmittedQuestion
            && !_windows.Confirm("Replace draft?", "Replace the unsent draft with this question?", "Replace draft")) return false;
        Composer.Question = message.SubmittedQuestion!;
        ErrorText = null;
        QuestionFocusRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

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

    public bool ShowSuggestions => ShowConversation && Messages.Count == 0 && string.IsNullOrWhiteSpace(Composer.Question);

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
                Composer.IsBusy = value;
                OnPropertyChanged(nameof(CanStartObjectChat));
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

    private bool CanSend => IsSignedIn && _consentGiven && !_isBusy && !string.IsNullOrWhiteSpace(Composer.Question) && Composer.SelectedModel is not null;

    private async Task ConnectAsync()
    {
        _isSigningIn = true;
        ErrorText = null;
        RaiseCommands();
        try
        {
            using var cancel = new CancellationTokenSource();
            var credentials = await _signIn.SignInAsync(_credentials ?? new ChatGptCredentials(), url => _windows.OpenUrl(url), cancel.Token).ConfigureAwait(true);

            Chats.SaveActive();

            _credentials = credentials;
            Chats.EnsureScope();
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
        Chats.SaveActive();
        _running?.Cancel();
        _store.Clear();
        _credentials = null;
        Chats.EnsureScope();
        _conversation.Clear();
        Messages.Clear();
        Models.Clear();
        Composer.SelectedModel = null;
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
            var keep = Chats.SelectedChat?.Data.Model ?? Composer.SelectedModel?.Slug;
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(model);
            }

            Composer.SelectedModel = Models.FirstOrDefault(m => m.Slug == keep)
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

        Chats.EnsureScope();

        var question = Composer.Question.Trim();
        Composer.Question = string.Empty;
        ErrorText = null;
        Messages.Add(IntelligenceMessageViewModel.User(question));
        var detail = Composer.SelectedDetail;
        var answer = IntelligenceMessageViewModel.Assistant(detail, question);
        answer.SeedInlineReferences(Messages.Where(message => message.IsAssistant).Reverse().SelectMany(message => message.InlineReferences));
        Messages.Add(answer);
        Chats.NameFromQuestion(question);
        Chats.SaveActive(recordActivity: true);
        OnPropertyChanged(nameof(ShowSuggestions));

        _running = new CancellationTokenSource();
        IsBusy = true;
        var listener = new Listener(this, answer);
        try
        {
            var spotlight = Context.SpotlightForAgent;
            var model = Composer.SelectedModel!.Slug;
            var token = _running.Token;
            var webSearchEnabled = IntelligenceConsent.AllowsWebSearch(_settings.Current);
            await Task.Run(() => _conversation.AskAsync(question, model, AccessTokenAsync, listener, token, spotlight, webSearchEnabled, detail)).ConfigureAwait(true);
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
            Chats.SaveActive(recordActivity: !string.IsNullOrWhiteSpace(answer.Text));
            Chats.EnsureScope();
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
        Chats.SaveActive();
        await _tools.StopAsync().ConfigureAwait(true);
        Chats.SaveActive();
    }

    /// <summary>Writes any chat saves still waiting - the app is closing.</summary>
    public void Dispose() => Chats.Dispose();

    private void Clear()
    {
        var previousChat = Chats.SelectedChat;
        Chats.NewChat();
        if (Chats.SelectedChat == previousChat) RotateSuggestions();

        // A new conversation starts on the default model.
        if (Models.FirstOrDefault(m => m.Slug == _defaultModel) is { } preferred)
        {
            Composer.SelectedModel = preferred;
        }

        ErrorText = null;
        OnPropertyChanged(nameof(ShowSuggestions));
        RaiseCommands();
        QuestionFocusRequested?.Invoke(this, EventArgs.Empty);
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
        Composer.SendCommand.RaiseCanExecuteChanged();
        EditQuestionCommand.RaiseCanExecuteChanged();
        RetryQuestionCommand.RaiseCanExecuteChanged();
        Composer.StopCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        AskSuggestionCommand.RaiseCanExecuteChanged();

        // The chat list is made after the commands, and raises its own once it exists.
        if (Chats is null) return;
        Chats.RaiseCommands();
        Composer.CanChangeChat = Chats.CanManageChats;
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
            _owner.Chats.SaveWhileStreaming();
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
            _owner.Chats.QueueSave();
        });

        public void OnStatusCard(IntelligenceStatusCard card) => _owner._dispatcher.InvokeAsync(() =>
        {
            _answer.AddStatusCard(card);
            _owner.Chats.QueueSave();
        });
    }
}
