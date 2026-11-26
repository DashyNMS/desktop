using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>Where an agent provider stands on this computer, as Settings and the picker show it.</summary>
public enum AgentProviderState
{
    /// <summary>Not looked for yet, or being checked now.</summary>
    Checking,

    /// <summary>No executable found, or the chosen one doesn't run.</summary>
    NotInstalled,

    /// <summary>Installed, but its own login is missing or has expired.</summary>
    SignedOut,

    Ready,

    /// <summary>Installed and signed in, but it can't be used right now - an unsupported version, say.</summary>
    Unavailable,
}

/// <summary>
/// What a check found (#316). <see cref="AccountKey"/> separates accounts -
/// chats saved under one are never shown under another - and is a digest,
/// never the account's name; <see cref="AccountText"/> is for Settings only.
/// </summary>
public sealed record AgentProviderStatus(
    AgentProviderState State,
    string? Executable = null,
    string? Version = null,
    string? AccountKey = null,
    string? AccountText = null,
    string? Detail = null)
{
    public static AgentProviderStatus Checking { get; } = new(AgentProviderState.Checking);

    public bool IsReady => State == AgentProviderState.Ready && !string.IsNullOrEmpty(AccountKey);
}

/// <summary>A model a provider offers, with the reasoning levels it accepts (empty when it has none to choose).</summary>
public sealed record AgentModel(string Id, string Name, IReadOnlyList<string> Efforts, string? DefaultEffort = null, string? Description = null);

/// <summary>
/// How much of a usage window has gone, as the provider reported it. Null
/// <see cref="UsedPercent"/> means the provider didn't say - never zero.
/// </summary>
public sealed record AgentUsageWindow(string Label, double? UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// Usage as last reported by the provider (#316): measured figures only, with
/// when they were seen. A provider that reports nothing has no usage at all,
/// rather than an estimate or a cost.
/// </summary>
public sealed record AgentUsage(IReadOnlyList<AgentUsageWindow> Windows, DateTimeOffset ObservedAt, bool LimitReached = false);

/// <summary>What a provider can do in DashyNMS - explained in the picker before anyone relies on it.</summary>
public sealed record AgentCapabilities(bool WebSearch, bool Images, bool Usage);

/// <summary>
/// One question for an agent. <see cref="Session"/> continues the agent's own
/// conversation, or null to start one; <see cref="Preamble"/> carries an earlier
/// conversation's visible questions and answers when the user moved it here.
/// <see cref="WebSearch"/> is Settings' web search choice, already checked
/// against the notice that describes it.
/// </summary>
public sealed record AgentTurn(
    string Question,
    string Model,
    string? Effort,
    string Instructions,
    string? Session,
    bool Temporary,
    string? Preamble = null,
    bool WebSearch = false);

/// <summary>The agent's session to continue next time, and its latest usage if it reported any.</summary>
public sealed record AgentTurnResult(string? Session, AgentUsage? Usage);

/// <summary>A provider's own failure, worded for the person: sign-in, limits, a missing install.</summary>
public sealed class AgentProviderException : Exception
{
    public AgentProviderException(string message, IntelligenceFailure failure, AgentProviderState? state = null, AgentUsage? usage = null)
        : base(message)
    {
        Failure = failure;
        State = state;
        Usage = usage;
    }

    public IntelligenceFailure Failure { get; }

    /// <summary>Set when the failure changes where the provider stands - signed out, or no longer installed.</summary>
    public AgentProviderState? State { get; }

    /// <summary>Set when the failure was a usage limit the provider described.</summary>
    public AgentUsage? Usage { get; }

    /// <summary>
    /// The agent no longer has the chat's session - a temporary chat after a
    /// restart, or history cleared outside DashyNMS. The chat carries on in a
    /// new session, with its visible conversation as context.
    /// </summary>
    public bool SessionLost { get; init; }
}

/// <summary>
/// A Desktop Intelligence agent provider (#316): an agent installed on this
/// computer - Codex or Claude Code - that answers with DashyNMS's read-only
/// tools through <see cref="AgentToolBridge"/>. It keeps its own sign-in and
/// configuration; DashyNMS layers a locked-down session over them and never
/// changes them.
/// </summary>
/// <remarks>
/// ChatGPT stays its own route in IntelligenceViewModel; this contract covers
/// the agents, which run their own tool loop. Text and tool activity reach the
/// panel through the turn's <see cref="IIntelligenceListener"/>: text from the
/// agent, tool activity from the bridge, which runs every call itself.
/// </remarks>
public interface IAgentProvider : IAsyncDisposable
{
    /// <summary>The settings id - see <see cref="Core.Configuration.AppSettings.IntelligenceProvider"/>.</summary>
    string Id { get; }

    /// <summary>The product's name, as the user knows it.</summary>
    string Name { get; }

    /// <summary>The company that processes what's sent, for the notice.</summary>
    string Company { get; }

    AgentCapabilities Capabilities { get; }

    /// <summary>Finds the executable (<paramref name="executablePath"/> when the user chose one), its version and its sign-in.</summary>
    Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken);

    /// <summary>The models this account can use. Only after a check found it ready.</summary>
    Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>Usage as the provider reports it now, or null when it reports none.</summary>
    Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Answers one question. Cancelling stops the agent's turn and discards
    /// anything it says afterwards.
    /// </summary>
    /// <exception cref="AgentProviderException">The provider refused or failed, worded for the person.</exception>
    Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken);

    /// <summary>
    /// Stops every running turn and process: the connection, account or
    /// settings changed, or Intelligence was turned off. The next question starts afresh.
    /// </summary>
    Task RetireAsync();
}
