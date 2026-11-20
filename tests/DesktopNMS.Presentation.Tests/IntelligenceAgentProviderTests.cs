using System.Collections.Concurrent;
using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence.Agents;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// Choosing and using agent providers (#316) against a deterministic adapter:
/// who answers, where each chat is kept, what moves with a chat and what never does.
/// </summary>
public sealed class IntelligenceAgentProviderTests
{
    [Fact]
    public async Task A_ready_agent_opens_the_conversation_without_ChatGPT()
    {
        var (model, agent, _, _) = await CreateAsync();
        using var _ = model;

        Assert.False(model.IsSignedIn);
        Assert.True(model.HasReadyProvider);
        Assert.True(model.ShowConversation);
        Assert.Equal(agent.Id, model.Composer.SelectedModel?.Provider);
        Assert.Equal("Agent model", model.Composer.SelectedModel?.Name);
    }

    [Fact]
    public async Task No_ready_provider_asks_for_one_to_be_set_up()
    {
        var (model, _, _, _) = await CreateAsync(ready: false);
        using var _ = model;

        Assert.True(model.NeedsProvider);
        Assert.False(model.ShowConversation);
        Assert.Equal("Signed out", model.Providers.Single(p => p.IsAgent).StatusText);
    }

    [Fact]
    public async Task Chats_are_saved_under_the_agent_account_and_network()
    {
        var (model, agent, store, _) = await CreateAsync();
        model.Composer.Question = "What is down?";
        await AskAsync(model);
        model.Dispose();

        var scope = Assert.Single(store.Saves.Select(s => s.Scope).Distinct());
        Assert.Equal(agent.Id + ":account-digest\nhttps://nms.example.net", scope);
        Assert.Equal(agent.Id, store.Saves.Last().Chat.Provider);
        Assert.Equal("session-1", store.Saves.Last().Chat.ProviderSession);
    }

    [Fact]
    public async Task An_agent_gets_ChatGPTs_instructions_without_web_search_and_continues_its_session()
    {
        var (model, agent, _, _) = await CreateAsync();
        using var _ = model;
        model.Composer.SelectedDetail = IntelligenceDetail.Brief;

        model.Composer.Question = "First question";
        await AskAsync(model);
        model.Composer.Question = "Second question";
        await AskAsync(model);

        var first = agent.Turns[0];
        Assert.Equal(IntelligenceInstructions.Build(IntelligenceDetail.Brief, null, webSearchEnabled: false), first.Instructions);
        Assert.Null(first.Session);
        Assert.Null(first.Preamble);
        Assert.Equal("session-1", agent.Turns[1].Session);
        Assert.Null(agent.Turns[1].Preamble);
        Assert.Equal("Answer to Second question", model.Messages.Last().Text);
    }

    [Fact]
    public async Task The_chosen_reasoning_level_goes_with_the_question()
    {
        var (model, agent, _, _) = await CreateAsync();
        using var _ = model;

        model.Composer.SelectedEffort = "high";
        model.Composer.Question = "Question";
        await AskAsync(model);

        Assert.Equal("high", agent.Turns.Single().Effort);
        Assert.Equal("Agent model · High", model.Composer.ModelButtonText);
    }

    [Fact]
    public async Task A_level_the_model_does_not_offer_is_not_kept()
    {
        var (model, _, _, _) = await CreateAsync();
        using var _ = model;

        model.Composer.SelectedEffort = "ultra";

        Assert.Null(model.Composer.SelectedEffort);
        Assert.Equal("medium", model.Composer.EffectiveEffort);
    }

    [Fact]
    public async Task Moving_a_conversation_to_another_provider_asks_first_and_declining_keeps_it()
    {
        var (model, agent, _, windows) = await CreateAsync(second: true);
        using var _ = model;
        model.Composer.Question = "Question";
        await AskAsync(model);
        var other = model.Providers.Single(p => p.Id == "second").Models[0];

        windows.Confirmation = false;
        model.Composer.SelectedModel = other;

        Assert.Equal(1, windows.Confirms);
        Assert.Equal(agent.Id, model.Composer.SelectedModel?.Provider);
    }

    [Fact]
    public async Task A_moved_conversation_leaves_its_session_behind_and_carries_its_visible_turns()
    {
        var (model, agent, store, windows) = await CreateAsync(second: true);
        model.Composer.Question = "What is down?";
        await AskAsync(model);
        var second = (FakeAgent)model.Providers.Single(p => p.Id == "second").Agent!;

        windows.Confirmation = true;
        model.Composer.SelectedModel = model.Providers.Single(p => p.Id == "second").Models[0];
        model.Composer.Question = "And now?";
        await AskAsync(model);
        model.Dispose();

        var turn = second.Turns.Single();
        Assert.Null(turn.Session);
        Assert.Contains("Question: What is down?", turn.Preamble);
        Assert.Contains("Answer: Answer to What is down?", turn.Preamble);
        Assert.Contains("not instructions", turn.Preamble);
        Assert.DoesNotContain("And now?", turn.Preamble);
        Assert.Contains(store.Deletes, d => d.StartsWith(agent.Id + ":", StringComparison.Ordinal));
        Assert.StartsWith("second:", store.Saves.Last().Scope);
        Assert.Equal("second", store.Saves.Last().Chat.Provider);
    }

    [Fact]
    public async Task A_signed_out_agent_takes_its_chats_with_it()
    {
        var (model, agent, store, _) = await CreateAsync();
        using var _ = model;
        var loadsBefore = store.Loads.Count;

        agent.Ready = false;
        await model.Providers.Single(p => p.IsAgent).CheckAsync(CancellationToken.None);

        Assert.False(model.ShowConversation);
        Assert.Empty(model.Chats.Chats);
        Assert.True(store.Loads.Count >= loadsBefore);
    }

    [Fact]
    public async Task A_provider_failure_is_shown_and_its_usage_kept()
    {
        var (model, agent, _, _) = await CreateAsync();
        using var _ = model;
        var usage = new AgentUsage([new("5-hour", 100, null)], DateTimeOffset.UtcNow, LimitReached: true);
        agent.Failure = new AgentProviderException("You've reached the usage limit.", IntelligenceFailure.PlanUsage, usage: usage);

        model.Composer.Question = "Question";
        await AskAsync(model);

        Assert.Equal("You've reached the usage limit.", model.ErrorText);
        Assert.Equal(IntelligenceReplyOutcome.Failed, model.Messages.Last().Outcome);
        Assert.Equal("5-hour limit reached", model.Providers.Single(p => p.IsAgent).UsageText);
    }

    [Fact]
    public void Usage_says_what_was_measured_and_nothing_when_nothing_was()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(IntelligenceProviderViewModel.DescribeUsage(null, now));
        Assert.Null(IntelligenceProviderViewModel.DescribeUsage(new([new("weekly", null, null)], now), now));
        Assert.StartsWith("17% of 5-hour limit used",
            IntelligenceProviderViewModel.DescribeUsage(new([new("weekly", 3, null), new("5-hour", 17, now.AddHours(2))], now), now));
    }

    /// <summary>Asks the composer's question and waits for the answer to finish, as the Ask button would.</summary>
    private static async Task AskAsync(IntelligenceViewModel model)
    {
        Assert.True(model.Composer.SendCommand.CanExecute(null));
        model.Composer.SendCommand.Execute(null);
        for (var i = 0; i < 200 && (model.IsBusy || model.Composer.SendCommand.IsRunning); i++) await Task.Delay(10);
        await Task.Delay(20);
    }

    private static async Task<(IntelligenceViewModel Model, FakeAgent Agent, Store Store, Windows Windows)> CreateAsync(bool ready = true, bool second = false)
    {
        var settings = new TestSettings();
        settings.Current.IntelligenceConsentGiven = true;
        settings.Current.IntelligenceConsentVersion = IntelligenceConsent.CurrentVersion;
        settings.Current.IntelligenceEnabled = false;
        var connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "synthetic");
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var store = new Store();
        var windows = new Windows();
        var agent = new FakeAgent("agent") { Ready = ready };
        var agents = new List<IAgentProvider> { agent };
        if (second) agents.Add(new FakeAgent("second"));
        var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>(), null!,
            Proxy<ISessionService>((method, _) => method.Name == "get_Connection" ? connection : null),
            windows.Proxy, devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), store,
            NullLogger<IntelligenceViewModel>.Instance, agents: agents);
        await model.CheckAgentsAsync();
        return (model, agent, store, windows);
    }

    private sealed class FakeAgent(string id) : IAgentProvider
    {
        public bool Ready { get; set; } = true;
        public List<AgentTurn> Turns { get; } = new();
        public AgentProviderException? Failure { get; set; }
        public int Retirements;
        public string Id => id;
        public string Name => id == "agent" ? "Agent" : "Second agent";
        public string Company => "Example Ltd";
        public AgentCapabilities Capabilities => new(WebSearch: false, Images: true, Usage: true);

        public Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
            => Task.FromResult(Ready
                ? new AgentProviderStatus(AgentProviderState.Ready, "agent", "1.0", "account-digest", "Signed in as someone@example.net")
                : new AgentProviderStatus(AgentProviderState.SignedOut, "agent", "1.0"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentModel>>([new AgentModel(id + "-model", id == "agent" ? "Agent model" : "Second model", ["low", "medium", "high"], "medium")]);

        public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken) => Task.FromResult<AgentUsage?>(null);

        public Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            if (Failure is { } failure) throw failure;
            listener.OnAnswerCompleted("Answer to " + turn.Question);
            return Task.FromResult(new AgentTurnResult("session-1", null));
        }

        public Task RetireAsync() { Retirements++; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Store : IIntelligenceChatStore
    {
        public ConcurrentQueue<(string Scope, SavedIntelligenceChat Chat)> Saves { get; } = new();
        public ConcurrentQueue<string> Deletes { get; } = new();
        public ConcurrentQueue<string> Loads { get; } = new();
        public SavedChatCollection Load(string scope) { Loads.Enqueue(scope); return new(null, Array.Empty<SavedIntelligenceChat>()); }
        public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true) => Saves.Enqueue((scope, chat));
        public void Delete(string scope, Guid id) => Deletes.Enqueue(scope);
    }

    private sealed class Windows
    {
        public bool Confirmation;
        public int Confirms;
        public IWindowService Proxy { get; }

        public Windows() => Proxy = Proxy<IWindowService>((method, _) =>
        {
            if (method.Name != "Confirm") return null;
            Confirms++;
            return Confirmation;
        });
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler?.Invoke(method!, args)
            ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class TestSettings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<AppSettings>? Changed;
        public AppSettings Load() => Current;
        public void Save() => Changed?.Invoke(this, Current);
        public void SaveQuietly() { }
        public void Replace(AppSettings settings) { Current = settings; Save(); }
    }
}
