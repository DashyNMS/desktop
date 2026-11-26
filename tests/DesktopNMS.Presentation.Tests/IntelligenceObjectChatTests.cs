using System.Reflection;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Row = DesktopNMS.Presentation.Tests.IntelligenceSpotlightAlertTests.Row;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Asking about an object changes chat only once its exact identity is confirmed.</summary>
public sealed class IntelligenceObjectChatTests
{
    [Fact]
    public async Task A_missing_object_keeps_the_current_chat_draft_and_spotlight()
    {
        using var server = new IntelligenceSpotlightAlertTests.AlertServer(faults: false, new Row(73, 12, 4, 2));
        using var model = Model(server, out var settings);
        var (chat, request) = Prepare(model, new("alert", 74, 12, 4));

        await model.StartObjectChatAsync(request);

        Assert.Contains("no longer available", model.ErrorText, StringComparison.Ordinal);
        Assert.Same(chat, model.Chats.SelectedChat);
        Assert.Equal("Half-written question", model.Composer.Question);
        Assert.Empty(model.Context.Spotlight);
    }

    [Fact]
    public async Task A_refused_check_keeps_the_current_chat_and_draft()
    {
        using var server = new IntelligenceSpotlightAlertTests.AlertServer(faults: true, new Row(73, 12, 4, 1, "port:5")) { RefuseAlerts = true };
        using var model = Model(server, out _);
        var (chat, request) = Prepare(model, new("fault", 73, 12, 4));

        await model.StartObjectChatAsync(request);

        Assert.Contains("Couldn't check access", model.ErrorText, StringComparison.Ordinal);
        Assert.Same(chat, model.Chats.SelectedChat);
        Assert.Equal("Half-written question", model.Composer.Question);
    }

    [Fact]
    public async Task A_switch_while_checking_discards_the_result_and_keeps_the_chat()
    {
        using var server = new IntelligenceSpotlightAlertTests.AlertServer(faults: false, new Row(73, 12, 4, 2));
        using var model = Model(server, out _, switchAfterLookup: true);
        var (chat, request) = Prepare(model, new("alert", 73, 12, 4));

        await model.StartObjectChatAsync(request);

        Assert.Same(chat, model.Chats.SelectedChat);
        Assert.Empty(model.Context.Spotlight);
    }

    [Fact]
    public async Task An_acknowledged_alert_is_confirmed_and_opens_a_chat_spotlighting_only_it()
    {
        using var server = new IntelligenceSpotlightAlertTests.AlertServer(faults: false, new Row(73, 12, 4, 2));
        using var model = Model(server, out _);
        var (chat, request) = Prepare(model, new("alert", 73, 12, 4));

        await model.StartObjectChatAsync(request);

        Assert.Null(model.ErrorText);
        Assert.NotSame(chat, model.Chats.SelectedChat);
        Assert.Equal(new IntelligenceObjectReference("alert", 73, 12, 4), Assert.Single(model.Context.Spotlight).Identity);
    }

    private static (IntelligenceChatItemViewModel? Chat, IntelligenceChatRequest Request) Prepare(IntelligenceViewModel model, IntelligenceObjectReference identity)
    {
        model.Chats.EnsureScope();
        model.Composer.Question = "Half-written question";
        var chat = model.Chats.SelectedChat;
        return (chat, new IntelligenceChatRequest(IntelligenceObjectTargets.Create(identity, "Object")!, model.ObjectServer!, model.ObjectChatScope));
    }

    private static IntelligenceViewModel Model(IntelligenceSpotlightAlertTests.AlertServer server, out Settings settings, bool switchAfterLookup = false)
    {
        settings = new Settings();
        settings.Current.IntelligenceEnabled = true;
        var credentials = new ChatGptCredentials { Subject = "synthetic-account", AccessToken = "synthetic", RefreshToken = "synthetic", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var connection = server.Connection;
        var other = new DesktopNMS.Core.Api.LibreNmsConnection(new Uri("https://other.example/"), "other");
        var lookups = 0;
        var client = Proxy<DesktopNMS.Core.Api.ILibreNmsClient>((method, args) =>
        {
            if (method.Name == "get_Alerts") lookups++;
            return method.Invoke(server.Client, args);
        });
        var session = Proxy<ISessionService>((method, _) => method.Name == "get_Connection" ? (switchAfterLookup && lookups > 0 ? other : connection) : null);
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>((method, _) => method.Name == "Load" ? credentials : null), null!,
            session, Proxy<IWindowService>(), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), new MemoryStore(),
            NullLogger<IntelligenceViewModel>.Instance);
        model.ConsentGiven = true;
        return model;
    }

    internal sealed class Settings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<AppSettings>? Changed;
        public AppSettings Load() => Current;
        public void Save() => Changed?.Invoke(this, Current);
        public void SaveQuietly() { }
        public void Replace(AppSettings settings) { Current = settings; Save(); }
    }

    private sealed class MemoryStore : IIntelligenceChatStore
    {
        private readonly Dictionary<Guid, SavedIntelligenceChat> _chats = new();
        public SavedChatCollection Load(string scope) => new(_chats.Keys.FirstOrDefault(), _chats.Values.ToList());
        public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true) => _chats[chat.Id] = chat;
        public void Delete(string scope, Guid id) => _chats.Remove(id);
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler?.Invoke(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}
