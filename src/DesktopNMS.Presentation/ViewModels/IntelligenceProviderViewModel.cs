using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services.Intelligence.Agents;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One Intelligence provider as the picker and Settings › Intelligence show it
/// (#316): whether it can answer, why not and what to do about it, its models
/// and its usage. ChatGPT's state comes from IntelligenceViewModel's own
/// sign-in; an agent's from checking its installation through
/// <see cref="IAgentProvider"/>.
/// </summary>
public sealed class IntelligenceProviderViewModel : ObservableObject
{
    private readonly ILogger? _logger;
    private readonly Func<string?>? _executablePath;
    private AgentProviderStatus _status = AgentProviderStatus.Checking;
    private AgentUsage? _usage;
    private int _checks;

    private IntelligenceProviderViewModel(string id, string name, string company, AgentCapabilities capabilities, IAgentProvider? agent, Func<string?>? executablePath, ILogger? logger)
    {
        Id = id;
        Name = name;
        Company = company;
        Capabilities = capabilities;
        Agent = agent;
        _executablePath = executablePath;
        _logger = logger;
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(CancellationToken.None), () => IsAgent && State != AgentProviderState.Checking);
    }

    /// <summary>ChatGPT, whose sign-in IntelligenceViewModel keeps.</summary>
    internal static IntelligenceProviderViewModel ChatGpt() => new(
        AppSettings.IntelligenceProviderChatGpt, "ChatGPT", "OpenAI", new AgentCapabilities(WebSearch: true, Images: true, Usage: false), null, null, null)
    {
        _status = new AgentProviderStatus(AgentProviderState.SignedOut),
    };

    internal static IntelligenceProviderViewModel ForAgent(IAgentProvider agent, Func<string?> executablePath, ILogger logger)
        => new(agent.Id, agent.Name, agent.Company, agent.Capabilities, agent, executablePath, logger);

    public string Id { get; }

    public string Name { get; }

    /// <summary>Who processes what's sent - named in the notice and Settings.</summary>
    public string Company { get; }

    public AgentCapabilities Capabilities { get; }

    /// <summary>What answers and whose account pays for it, for Settings.</summary>
    public string Description => Id switch
    {
        AppSettings.IntelligenceProviderCodex => "Uses Codex on this computer with its existing sign-in: your ChatGPT plan or OpenAI API account. DashyNMS gives it only the read-only network tools; its own tools and your Codex settings are left out of these chats.",
        AppSettings.IntelligenceProviderClaudeCode => "Uses Claude Code on this computer with its existing sign-in: your Claude plan or Anthropic API account. DashyNMS gives it only the read-only network tools; its own tools and your Claude Code settings are left out of these chats.",
        AppSettings.IntelligenceProviderCopilot => "Uses the GitHub Copilot CLI on this computer with its existing sign-in, on any Copilot plan. Questions go to GitHub, then to the company that makes the chosen model, and count against your plan's AI credits.",
        AppSettings.IntelligenceProviderGemini => "Uses Antigravity CLI on this computer with your Google account: free, AI Pro or Ultra. Questions go to Google and count against your plan's quota.",
        _ => "Uses your own ChatGPT Plus or Pro plan. Sign in through your browser - DashyNMS never sees your ChatGPT password.",
    };

    internal IAgentProvider? Agent { get; }

    public bool IsAgent => Agent is not null;

    public bool IsChatGpt => Id == AppSettings.IntelligenceProviderChatGpt;

    public ObservableCollection<IntelligenceModelOption> Models { get; } = new();

    private bool _isOffered;
    private IReadOnlyCollection<string> _hiddenModels = Array.Empty<string>();

    /// <summary>
    /// Offered in the model picker and chat list: ready, switched on in
    /// Settings, and - for ChatGPT's two routes - the connection chosen there.
    /// Set by IntelligenceViewModel, which knows all three.
    /// </summary>
    public bool IsOffered
    {
        get => _isOffered;
        internal set
        {
            if (SetProperty(ref _isOffered, value)) StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The models the picker shows: all but those hidden in Settings.</summary>
    public IReadOnlyList<IntelligenceModelOption> VisibleModels => Models.Where(m => !_hiddenModels.Contains(m.Id)).ToList();

    internal void SetHiddenModels(IEnumerable<string>? hidden)
    {
        var list = (hidden ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        if (list.SetEquals(_hiddenModels)) return;
        _hiddenModels = list;
        OnPropertyChanged(nameof(VisibleModels));
    }

    /// <summary>
    /// The name people see. Codex is one of ChatGPT's two connections, so it
    /// shows as ChatGPT; only one of them is ever offered at a time.
    /// </summary>
    public string DisplayName => Id == AppSettings.IntelligenceProviderCodex ? "ChatGPT" : Name;

    private bool _isPickerTab;

    /// <summary>This provider's tab is the one open in the composer's model picker.</summary>
    public bool IsPickerTab
    {
        get => _isPickerTab;
        internal set => SetProperty(ref _isPickerTab, value);
    }

    public AsyncRelayCommand CheckCommand { get; }

    public AgentProviderStatus Status
    {
        get => _status;
        internal set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsChecking));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(SetupCommandText));
            OnPropertyChanged(nameof(HasSetupCommand));
            OnPropertyChanged(nameof(AccountText));
            OnPropertyChanged(nameof(VersionText));
            CheckCommand.RaiseCanExecuteChanged();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when whether it can answer, or for which account, changes.</summary>
    public event EventHandler? StatusChanged;

    public AgentProviderState State => _status.State;

    /// <summary>Signed in, with an account to keep its chats under.</summary>
    public bool IsReady => IsChatGpt ? _status.State == AgentProviderState.Ready : _status.IsReady;

    public bool IsChecking => _status.State == AgentProviderState.Checking;

    public string StatusText => _status.State switch
    {
        AgentProviderState.Checking => "Checking…",
        AgentProviderState.NotInstalled => "Not installed",
        AgentProviderState.SignedOut => IsChatGpt ? "Not connected" : "Signed out",
        AgentProviderState.Ready => "Ready",
        _ => "Unavailable",
    };

    /// <summary>What's wrong and what to do about it - shown in Settings and, short, in the picker.</summary>
    public string? DetailText => _status.Detail ?? _status.State switch
    {
        AgentProviderState.NotInstalled => Id switch
        {
            AppSettings.IntelligenceProviderCodex => "Install Codex, sign in once, then choose Check again. If it's installed somewhere DashyNMS can't find, choose its executable below.",
            AppSettings.IntelligenceProviderClaudeCode => "Install Claude Code, sign in once, then choose Check again. If it's installed somewhere DashyNMS can't find, choose its executable below.",
            AppSettings.IntelligenceProviderCopilot => "Install the GitHub Copilot CLI, sign in once, then choose Check again. VS Code's copilot command only offers to install it, so DashyNMS looks for the CLI itself.",
            AppSettings.IntelligenceProviderGemini => OperatingSystem.IsWindows()
                ? "Install Antigravity CLI, Google's terminal agent, sign in once with your Google account, then choose Check again."
                : "Install Antigravity CLI, Google's terminal agent, with the installer on antigravity.google, sign in once, then choose Check again.",
            _ => null,
        },
        AgentProviderState.SignedOut when !IsChatGpt => $"{Name} is installed but not signed in. Sign in with the command below in a terminal, then choose Check again.",
        _ => null,
    };

    public bool HasDetail => !string.IsNullOrEmpty(DetailText);

    /// <summary>The command that fixes the current state, for the Copy button - never anything DashyNMS runs itself.</summary>
    public string? SetupCommandText => (_status.State, Id) switch
    {
        (AgentProviderState.NotInstalled, AppSettings.IntelligenceProviderCodex) => "npm install -g @openai/codex",
        (AgentProviderState.NotInstalled, AppSettings.IntelligenceProviderClaudeCode) => OperatingSystem.IsWindows()
            ? "winget install Anthropic.ClaudeCode"
            : "curl -fsSL https://claude.ai/install.sh | bash",
        (AgentProviderState.SignedOut, AppSettings.IntelligenceProviderCodex) => "codex login",
        (AgentProviderState.SignedOut, AppSettings.IntelligenceProviderClaudeCode) => "claude auth login",
        (AgentProviderState.NotInstalled, AppSettings.IntelligenceProviderCopilot) => OperatingSystem.IsWindows()
            ? "winget install GitHub.Copilot"
            : "npm install -g @github/copilot",
        (AgentProviderState.SignedOut, AppSettings.IntelligenceProviderCopilot) => "copilot login",
        (AgentProviderState.NotInstalled, AppSettings.IntelligenceProviderGemini) when OperatingSystem.IsWindows() => "winget install Google.AntigravityCLI",
        (AgentProviderState.SignedOut, AppSettings.IntelligenceProviderGemini) => "agy",
        _ => null,
    };

    public bool HasSetupCommand => SetupCommandText is not null;

    /// <summary>Settings only: whose account answers. Never on the Intelligence tab itself.</summary>
    public string? AccountText => _status.AccountText;

    public string? VersionText => _status.Version is { } version ? "Version " + version : null;

    /// <summary>Usage as the provider last reported it, or null when it hasn't - never a guess.</summary>
    public AgentUsage? Usage
    {
        get => _usage;
        internal set
        {
            if (SetProperty(ref _usage, value))
            {
                OnPropertyChanged(nameof(UsageText));
                OnPropertyChanged(nameof(HasUsage));
            }
        }
    }

    public bool HasUsage => UsageText is not null;

    /// <summary>
    /// The tightest window the provider reported, in plain words: "17% of the
    /// 5-hour limit used · resets 18:30". Null when nothing was reported, so the
    /// interface says usage isn't available rather than inventing a figure.
    /// </summary>
    public string? UsageText => DescribeUsage(_usage, DateTimeOffset.Now);

    internal static string? DescribeUsage(AgentUsage? usage, DateTimeOffset now)
    {
        if (usage is null) return null;
        var window = usage.Windows.Where(w => w.UsedPercent is not null).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
        if (window is null) return usage.LimitReached ? "Usage limit reached" : null;
        var percent = Math.Clamp(window.UsedPercent!.Value, 0, 100).ToString("0", CultureInfo.CurrentCulture);
        var text = usage.LimitReached && window.UsedPercent >= 100
            ? $"{window.Label} limit reached"
            : $"{percent}% of {window.Label} limit used";
        if (window.ResetsAt is { } resets && resets > now)
        {
            var local = resets.ToLocalTime();
            text += " · resets " + (local.Date == now.ToLocalTime().Date
                ? local.ToString("t", CultureInfo.CurrentCulture)
                : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture));
        }

        return text;
    }

    /// <summary>
    /// Checks the agent again: installation, version and sign-in, then its
    /// models and usage. Only the latest check's result is kept.
    /// </summary>
    /// <param name="executablePath">An executable to check instead of the saved one - Settings, before it's saved.</param>
    public async Task CheckAsync(CancellationToken cancellationToken, string? executablePath = null)
    {
        if (Agent is null) return;
        var check = Interlocked.Increment(ref _checks);
        Status = AgentProviderStatus.Checking;
        AgentProviderStatus status;
        IReadOnlyList<AgentModel> models = Array.Empty<AgentModel>();
        AgentUsage? usage = null;
        try
        {
            status = await Agent.CheckAsync(executablePath ?? _executablePath?.Invoke(), cancellationToken).ConfigureAwait(true);
            if (status.IsReady)
            {
                models = await Agent.ListModelsAsync(cancellationToken).ConfigureAwait(true);
                usage = await Agent.ReadUsageAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // An agent's output can hold paths or account detail: the type is enough to diagnose.
            _logger?.LogWarning("Intelligence: checking {Provider} failed ({ErrorType})", Id, ex.GetType().Name);
            status = new AgentProviderStatus(AgentProviderState.Unavailable, Detail: $"DashyNMS couldn't check {Name}. Make sure it runs in a terminal, then choose Check again.");
        }

        if (check != Volatile.Read(ref _checks)) return;
        ReplaceModels(models.Select(m => IntelligenceModelOption.FromAgent(Id, m)));
        Usage = usage;
        Status = status;
    }

    internal void ReplaceModels(IEnumerable<IntelligenceModelOption> models)
    {
        var list = models.ToList();
        if (list.SequenceEqual(Models) && list.Select(m => m.Name).SequenceEqual(Models.Select(m => m.Name))) return;
        Models.Clear();
        foreach (var model in list) Models.Add(model);
    }
}
