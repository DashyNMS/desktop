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

/// <summary>Settings › Providers (#316): what's listed, what's selected, and removing.</summary>
public sealed class IntelligenceProvidersSettingsTests
{
    [Fact]
    public async Task Checking_a_provider_again_keeps_it_listed_and_only_it_selected()
    {
        var (providers, agent, _) = await CreateAsync();
        var entry = providers.Listed.Single(e => e.Id == agent.Id);
        providers.Selected = entry;

        agent.Gate = new TaskCompletionSource();
        var check = entry.Route!.CheckAsync(CancellationToken.None);
        Assert.Contains(entry, providers.Listed);
        agent.Gate.SetResult();
        await check;

        Assert.Same(entry, providers.Selected);
        Assert.Equal([agent.Id], providers.All.Where(e => e.IsSelected).Select(e => e.Id));
    }

    [Fact]
    public async Task A_removed_provider_leaves_the_list_even_when_installed()
    {
        var (providers, agent, draft) = await CreateAsync();
        var entry = providers.Listed.Single(e => e.Id == agent.Id);

        entry.RemoveCommand.Execute(null);

        Assert.DoesNotContain(entry, providers.Listed);
        Assert.False(draft.IntelligenceProviders[agent.Id].Added);
        Assert.Equal("Add", entry.ActionText);
    }

    [Fact]
    public async Task An_uninstalled_provider_offers_its_install_page()
    {
        var (providers, agent, _) = await CreateAsync(state: AgentProviderState.NotInstalled);
        var entry = providers.All.Single(e => e.Id == agent.Id);

        Assert.DoesNotContain(entry, providers.Listed);
        Assert.Equal("Install", entry.ActionText);
        Assert.True(entry.ShowInstall);
    }

    private static async Task<(IntelligenceProvidersViewModel Providers, FakeAgent Agent, AppSettings Draft)> CreateAsync(AgentProviderState state = AgentProviderState.Ready)
    {
        var settings = new TestSettings();
        settings.Current.IntelligenceEnabled = false;
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var agent = new FakeAgent(AppSettings.IntelligenceProviderClaudeCode) { State = state };
        var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>(), null!, Proxy<ISessionService>(), Proxy<IWindowService>(), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), Proxy<IIntelligenceChatStore>(),
            NullLogger<IntelligenceViewModel>.Instance, agents: [agent]);
        await model.CheckAgentsAsync();
        var draft = settings.Current.Clone();
        return (new IntelligenceProvidersViewModel(model, draft), agent, draft);
    }

    private sealed class FakeAgent(string id) : IAgentProvider
    {
        public AgentProviderState State { get; set; } = AgentProviderState.Ready;
        public TaskCompletionSource? Gate { get; set; }
        public string Id => id;
        public string Name => "Claude Code";
        public string Company => "Example Ltd";
        public AgentCapabilities Capabilities => new(false, false, false);

        public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
        {
            if (Gate is { } gate) await gate.Task;
            return State == AgentProviderState.Ready ? new AgentProviderStatus(State, "agent", "1.0", "digest") : new AgentProviderStatus(State);
        }

        public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentModel>>([]);
        public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken) => Task.FromResult<AgentUsage?>(null);
        public Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RetireAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
