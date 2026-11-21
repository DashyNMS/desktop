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
    public async Task The_picker_offers_only_providers_that_can_answer()
    {
        var (model, _, _, _) = await CreateAsync(second: true);
        using var _ = model;
        var second = model.Providers.Single(p => p.Id == "second");
        ((FakeAgent)second.Agent!).Ready = false;
        await second.CheckAsync(CancellationToken.None);

        Assert.Equal(["agent"], model.Composer.AvailableProviders.Select(p => p.Id));
        Assert.False(model.Composer.HasProviderTabs);
        Assert.DoesNotContain(model.Composer.AvailableProviders, p => p.IsChatGpt);
        Assert.Equal("agent", model.Composer.PickerProvider?.Id);
    }

    [Fact]
    public async Task The_reasoning_row_follows_the_open_tab()
    {
        var (model, _, _, _) = await CreateAsync(second: true, secondHasEfforts: false);
        using var _ = model;
        var agent = model.Providers.Single(p => p.Id == "agent");
        var second = model.Providers.Single(p => p.Id == "second");
        Assert.Equal("agent", model.Composer.SelectedModel?.Provider);

        // A tab whose models have no levels hides the row, even while a model with levels is chosen.
        model.Composer.ShowProviderCommand.Execute(second);
        Assert.False(model.Composer.HasEfforts);

        // Back on the chosen model's tab, its levels show straight away.
        model.Composer.ShowProviderCommand.Execute(agent);
        Assert.True(model.Composer.HasEfforts);
        Assert.Equal("medium", Assert.Single(model.Composer.Efforts, e => e.IsSelected).Key);
    }

    [Fact]
    public async Task The_picker_reopens_on_the_chats_provider_with_no_search()
    {
        var (model, _, _, _) = await CreateAsync(second: true);
        using var _ = model;
        model.Composer.ShowProviderCommand.Execute(model.Providers.Single(p => p.Id == "second"));
        model.Composer.ModelSearch = "nothing";

        model.Composer.ResetPicker();

        Assert.Equal("agent", model.Composer.PickerProvider?.Id);
        Assert.Empty(model.Composer.ModelSearch);
    }

    [Fact]
    public async Task A_level_on_another_providers_tab_chooses_that_tabs_model()
    {
        var (model, _, _, _) = await CreateAsync(second: true);
        using var _ = model;
        var second = model.Providers.Single(p => p.Id == "second");

        model.Composer.ShowProviderCommand.Execute(second);
        Assert.True(model.Composer.HasEfforts);
        Assert.Equal("Second model", model.Composer.ReasoningModel?.Name);
        model.Composer.SelectEffortCommand.Execute("high");

        Assert.Equal("second", model.Composer.SelectedModel?.Provider);
        Assert.Equal("high", model.Composer.SelectedEffort);
    }

    [Fact]
    public async Task A_provider_removed_in_Settings_leaves_the_picker()
    {
        var (model, _, _, _) = await CreateAsync(second: true);
        using var _ = model;

        Settings.Current.IntelligenceProviders["second"] = new() { Added = false };
        Settings.Save();

        Assert.DoesNotContain(model.Composer.AvailableProviders, p => p.Id == "second");
        Assert.Contains(model.Composer.AvailableProviders, p => p.Id == "agent");
    }

    [Fact]
    public async Task Removing_ChatGPT_disconnects_its_own_sign_in_and_signing_in_lists_it_again()
    {
        var credentials = new ChatGptCredentials { Subject = "synthetic-account", AccessToken = "synthetic", RefreshToken = "synthetic", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var (model, _, _, _) = await CreateAsync(credentials: credentials);
        using var _ = model;
        Assert.True(model.IsSignedIn);

        Settings.Current.IntelligenceProviders[AppSettings.IntelligenceProviderChatGpt] = new() { Added = false };
        Settings.Save();
        for (var i = 0; i < 100 && model.IsSignedIn; i++) await Task.Delay(10);

        Assert.False(model.IsSignedIn);
        Assert.Contains(model.Composer.AvailableProviders, p => p.Id == "agent");
    }

    [Fact]
    public async Task Hidden_models_leave_the_picker_and_new_chats()
    {
        var (model, _, _, _) = await CreateAsync();
        using var _ = model;

        Settings.Current.IntelligenceProviders["agent"] = new() { HiddenModels = ["agent-model"] };
        Settings.Save();

        Assert.Empty(model.Composer.PickerModels);
        Assert.Null(model.DefaultModel("agent"));
    }

    [Fact]
    public async Task Only_the_chosen_ChatGPT_connection_is_offered()
    {
        var (model, _, _, _) = await CreateAsync(id: AppSettings.IntelligenceProviderCodex);
        using var _ = model;

        Assert.Equal(AppSettings.IntelligenceConnectionCodex, model.ChatGptConnection);
        Assert.Contains(model.Composer.AvailableProviders, p => p.Id == AppSettings.IntelligenceProviderCodex);
        Assert.Equal("ChatGPT", model.Providers.Single(p => p.Id == AppSettings.IntelligenceProviderCodex).DisplayName);

        Settings.Current.IntelligenceProviders[AppSettings.IntelligenceProviderChatGpt] = new() { Connection = AppSettings.IntelligenceConnectionSignIn };
        Settings.Save();

        Assert.DoesNotContain(model.Composer.AvailableProviders, p => p.Id == AppSettings.IntelligenceProviderCodex);
        Assert.True(model.NeedsProvider);
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
    public async Task Web_search_follows_the_setting_and_its_notice()
    {
        var (model, agent, _, _) = await CreateAsync(webSearch: true);
        using var _ = model;

        model.Composer.Question = "Latest firmware?";
        await AskAsync(model);

        Assert.True(agent.Turns.Single().WebSearch);
        Assert.Equal(IntelligenceInstructions.Build(IntelligenceDetail.Normalize(null), null, webSearchEnabled: true), agent.Turns.Single().Instructions);
    }

    [Fact]
    public async Task Web_search_stays_off_when_Settings_turns_it_off()
    {
        var (model, agent, _, _) = await CreateAsync(webSearch: false);
        using var _ = model;

        model.Composer.Question = "Latest firmware?";
        await AskAsync(model);

        Assert.False(agent.Turns.Single().WebSearch);
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
        Assert.DoesNotContain("Agent changed", turn.Preamble);

        // The transcript, and the saved chat, mark where it changed hands.
        var handoff = Assert.Single(model.Messages, m => m.IsHandoff).Handoff!;
        Assert.Equal((agent.Id, "second"), (handoff.FromProvider, handoff.ToProvider));
        Assert.Single(store.Saves.Last().Chat.Messages, m => m.Handoff is not null);
    }

    [Fact]
    public async Task Changes_before_the_next_question_fold_into_one_divider_and_none_once_back()
    {
        var (model, agent, _, windows) = await CreateAsync(second: true);
        using var _ = model;
        model.Composer.Question = "Question";
        await AskAsync(model);
        windows.Confirmation = true;
        var original = model.Composer.SelectedModel!;

        model.Composer.SelectedModel = model.Providers.Single(p => p.Id == "second").Models[0];
        Assert.True(model.Messages[^1].IsHandoff);
        Assert.Equal(original.Name, model.Messages[^1].Handoff!.FromModel);

        model.Composer.SelectedModel = original;
        Assert.DoesNotContain(model.Messages, m => m.IsHandoff);
        Assert.Equal(agent.Id, model.Composer.SelectedModel?.Provider);
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

    /// <summary>The settings of the model the last CreateAsync made - tests in a class run one at a time.</summary>
    private static TestSettings Settings = null!;

    /// <summary>Asks the composer's question and waits for the answer to finish, as the Ask button would.</summary>
    private static async Task AskAsync(IntelligenceViewModel model)
    {
        Assert.True(model.Composer.SendCommand.CanExecute(null));
        model.Composer.SendCommand.Execute(null);
        for (var i = 0; i < 200 && (model.IsBusy || model.Composer.SendCommand.IsRunning); i++) await Task.Delay(10);
        await Task.Delay(20);
    }

    private static async Task<(IntelligenceViewModel Model, FakeAgent Agent, Store Store, Windows Windows)> CreateAsync(bool ready = true, bool second = false, bool secondHasEfforts = true, bool webSearch = false, string id = "agent", ChatGptCredentials? credentials = null)
    {
        var settings = new TestSettings();
        Settings = settings;
        settings.Current.IntelligenceConsentGiven = true;
        settings.Current.IntelligenceConsentVersion = IntelligenceConsent.CurrentVersion;
        settings.Current.IntelligenceEnabled = false;
        settings.Current.IntelligenceWebSearchEnabled = webSearch;
        var connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "synthetic");
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var store = new Store();
        var windows = new Windows();
        var agent = new FakeAgent(id) { Ready = ready };
        var agents = new List<IAgentProvider> { agent };
        if (second) agents.Add(new FakeAgent("second") { HasEfforts = secondHasEfforts });
        var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>((method, _) => method.Name == "Load" ? credentials : null), null!,
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
        public bool HasEfforts { get; init; } = true;
        public List<AgentTurn> Turns { get; } = new();
        public AgentProviderException? Failure { get; set; }
        public int Retirements;
        public string Id => id;
        public string Name => id == "agent" ? "Agent" : "Second agent";
        public string Company => "Example Ltd";
        public AgentCapabilities Capabilities => new(WebSearch: true, Images: true, Usage: true);

        public Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
            => Task.FromResult(Ready
                ? new AgentProviderStatus(AgentProviderState.Ready, "agent", "1.0", "account-digest", "Signed in as someone@example.net")
                : new AgentProviderStatus(AgentProviderState.SignedOut, "agent", "1.0"));

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AgentModel>>([new AgentModel(id + "-model", id == "agent" ? "Agent model" : "Second model",
                HasEfforts ? ["low", "medium", "high"] : Array.Empty<string>(), HasEfforts ? "medium" : null)]);

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
