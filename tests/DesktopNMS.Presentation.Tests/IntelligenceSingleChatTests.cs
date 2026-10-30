using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Spotlights;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class IntelligenceSingleChatTests
{
    [Fact]
    public void A_queued_timer_tick_after_enabling_single_chat_cannot_write_the_draft()
    {
        var timer = new TestTimer();
        var host = new Host();
        var store = new RecordingStore();
        using var chats = new IntelligenceChatListViewModel(host, store, Proxy<IWindowService>(), UiThread.Dispatcher, NullLogger.Instance, timer);
        chats.EnsureScope();
        chats.QueueSave();
        Assert.True(timer.IsEnabled);
        host.SingleChatMode = true;
        host.FillTemporary();
        timer.FireQueuedTick();
        chats.EnsureScope();
        Assert.False(timer.IsEnabled);
        Assert.All(store.Saved, chat => Assert.DoesNotContain("temporary", chat.Draft));
    }

    [Fact]
    public void Real_conversation_reset_clears_draft_messages_history_spotlight_and_companion()
    {
        var settings = new TestSettings();
        var credentials = new ChatGptCredentials { Subject = "synthetic-account" };
        var connection = new LibreNmsConnection(new Uri("https://nms.example.net/"), "synthetic");
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var store = new RecordingStore();
        using var model = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>((method, _) => method.Name == "Load" ? credentials : null), null!,
            Proxy<ISessionService>((method, _) => method.Name == "get_Connection" ? connection : null),
            Proxy<IWindowService>(), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), store,
            NullLogger<IntelligenceViewModel>.Instance);
        credentials.AccessToken = credentials.RefreshToken = "synthetic";
        model.ConsentGiven = true;
        var host = (IIntelligenceChatHost)model;
        model.Composer.Question = "Latest persistent draft";
        model.PrepareChatModeChange(true);
        settings.Current.IntelligenceSingleChatMode = true;
        settings.Save();
        Assert.True(model.Composer.CanChangeChat);
        Assert.False(model.ShowSavedChats);
        host.Restore(new()
        {
            Draft = "temporary draft", Messages = [new() { IsUser = true, Text = "temporary message" }],
            History = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "temporary history" }),
            Spotlight = [new((int)SpotlightKind.Device, 1, "temporary device")],
            LookedAt = ["temporary lookup"], Outfit = (DashyOutfit)1
        });
        var captured = new SavedIntelligenceChat();
        host.Capture(captured);
        Assert.NotEmpty(captured.History);
        Assert.NotEmpty(captured.Spotlight);
        model.ClearCommand.Execute(null);
        captured = new();
        host.Capture(captured);
        Assert.Empty(captured.Draft);
        Assert.Empty(captured.Messages);
        Assert.Empty(captured.History);
        Assert.Empty(captured.Spotlight);
        Assert.Empty(captured.LookedAt);
        settings.Current.IntelligenceSingleChatMode = false;
        settings.Save();
        Assert.Equal("Latest persistent draft", model.Composer.Question);
        Assert.True(model.IsSignedIn);
        Assert.True(model.ShowSavedChats);
        Assert.All(store.Saved, chat => Assert.DoesNotContain("temporary", chat.Draft));
    }

    [Fact]
    public void Already_queued_persistent_writes_cannot_capture_temporary_content()
    {
        using var gate = new ManualResetEventSlim(false);
        var store = new RecordingStore { Gate = gate };
        var host = new Host();
        using var chats = Create(host, store);
        chats.EnsureScope();
        Assert.True(store.Started.Wait(TimeSpan.FromSeconds(5)));
        host.Current.Draft = "persistent draft";
        chats.SaveActive();
        host.SingleChatMode = true;
        host.FillTemporary();
        chats.QueueSave();
        chats.SaveActive();
        gate.Set();
        chats.EnsureScope();
        Assert.All(store.Saved, chat => Assert.DoesNotContain("temporary", chat.Draft));
        Assert.Contains(store.Saved, chat => chat.Draft == "persistent draft");
    }

    [Fact]
    public void Temporary_chats_never_touch_the_store_and_restart_with_no_conversation_state()
    {
        var store = new RecordingStore();
        var host = new Host { SingleChatMode = true };
        using (var chats = Create(host, store))
        {
            chats.EnsureScope();
            Assert.Single(chats.Chats);
            Assert.True(chats.CanEditConversation);
            Assert.False(chats.CanManageChats);
            host.FillTemporary();
            chats.QueueSave();
            chats.SaveWhileStreaming();
            chats.SaveActive(recordActivity: true);
            chats.PinChatCommand.Execute(chats.SelectedChat);
            chats.DeleteChatCommand.Execute(chats.SelectedChat);
        }
        var restarted = new Host { SingleChatMode = true };
        using var next = Create(restarted, store);
        next.EnsureScope();
        AssertBlank(restarted.Current);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void Mode_changes_keep_the_archive_and_discard_all_temporary_content()
    {
        var store = new RecordingStore();
        var host = new Host();
        using var chats = Create(host, store);
        chats.EnsureScope();
        Assert.Equal("Saved draft", host.Current.Draft);
        chats.SaveActive();
        host.SingleChatMode = true;
        chats.EnsureScope();
        var before = store.Calls.ToArray();
        AssertBlank(host.Current);
        host.FillTemporary();
        chats.QueueSave();
        chats.SaveActive();
        chats.NewChat(force: true);
        Assert.Single(chats.Chats);
        AssertBlank(host.Current);
        host.FillTemporary();
        host.SingleChatMode = false;
        chats.SaveActive();
        chats.EnsureScope();
        Assert.Equal("Saved draft", host.Current.Draft);
        Assert.True(chats.CanManageChats);
        Assert.All(store.Saved, chat => Assert.DoesNotContain("temporary", chat.Draft));
        Assert.DoesNotContain(store.Calls, call => call == "delete");
        Assert.Equal(before.Count(call => call == "load") + 1, store.Calls.Count(call => call == "load"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Busy_mode_changes_cancel_before_replacing_and_never_save_the_wrong_conversation(bool enable)
    {
        var store = new RecordingStore();
        var host = new Host { SingleChatMode = !enable };
        using var chats = Create(host, store);
        chats.EnsureScope();
        var selected = chats.SelectedChat;
        host.IsBusy = true;
        host.SingleChatMode = enable;
        chats.EnsureScope();
        Assert.Equal(1, host.Cancellations);
        Assert.Same(selected, chats.SelectedChat);
        Assert.False(chats.CanEditConversation);
        host.FillTemporary();
        chats.QueueSave();
        chats.SaveWhileStreaming();
        host.IsBusy = false;
        chats.SaveActive(recordActivity: true);
        chats.EnsureScope();
        Assert.NotSame(selected, chats.SelectedChat);
        Assert.All(store.Saved, chat => Assert.DoesNotContain("temporary", chat.Draft));
        if (enable) AssertBlank(host.Current);
        else Assert.Equal("Saved draft", host.Current.Draft);
    }

    [Fact]
    public void Account_and_server_changes_discard_temporary_context_without_loading_an_archive()
    {
        var store = new RecordingStore();
        var host = new Host { SingleChatMode = true };
        using var chats = Create(host, store);
        chats.EnsureScope();
        foreach (var scope in new[] { "other-account\nhttps://nms.example.net", null, "account\nhttps://other.example.net" })
        {
            host.FillTemporary();
            host.ChatScope = scope;
            chats.EnsureScope();
            AssertBlank(host.Current);
            Assert.Equal(scope is null ? 0 : 1, chats.Chats.Count);
        }
        Assert.Empty(store.Calls);
    }

    private static void AssertBlank(SavedIntelligenceChat chat)
    {
        Assert.Empty(chat.Draft);
        Assert.Empty(chat.Messages);
        Assert.Empty(chat.History);
        Assert.Empty(chat.Spotlight);
        Assert.Empty(chat.LookedAt);
        Assert.Equal(DashyOutfit.Normal, chat.Outfit);
    }

    private static IntelligenceChatListViewModel Create(Host host, IIntelligenceChatStore store)
        => new(host, store, Proxy<IWindowService>(), UiThread.Dispatcher, NullLogger.Instance);

    private sealed class Host : IIntelligenceChatHost
    {
        public bool IsBusy { get; set; }
        public bool ShowConversation => true;
        public bool SingleChatMode { get; set; }
        public string? ChatScope { get; set; } = "account\nhttps://nms.example.net";
        public string? ErrorText { get; set; }
        public int Cancellations;
        public SavedIntelligenceChat Current = new();
        public void CancelAnswer() => Cancellations++;
        public void Capture(SavedIntelligenceChat chat)
        {
            chat.Draft = Current.Draft;
            chat.Messages = Current.Messages;
            chat.History = Current.History;
            chat.Spotlight = Current.Spotlight;
            chat.LookedAt = Current.LookedAt;
            chat.Outfit = Current.Outfit;
        }
        public void Restore(SavedIntelligenceChat? chat) => Current = chat ?? new();
        public SavedIntelligenceChat NewChat() => new();
        public void FillTemporary()
        {
            Current.Draft = "temporary draft";
            Current.Messages = [new() { Text = "temporary message" }];
            Current.History = new JsonArray(new JsonObject { ["role"] = "tool", ["content"] = "temporary tool output" });
            Current.Spotlight = [new(0, 1, "temporary Spotlight")];
            Current.LookedAt = ["temporary observation"];
            Current.Outfit = (DashyOutfit)1;
        }
    }

    private sealed class RecordingStore : IIntelligenceChatStore
    {
        public ManualResetEventSlim? Gate;
        public ManualResetEventSlim Started = new(false);
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<SavedIntelligenceChat> Saved { get; } = new();
        public SavedChatCollection Load(string scope)
        {
            Calls.Enqueue("load");
            var archived = Saved.LastOrDefault() ?? new SavedIntelligenceChat { Draft = "Saved draft" };
            return new(archived.Id, [archived]);
        }
        public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true)
        {
            Started.Set();
            Gate?.Wait(TimeSpan.FromSeconds(5));
            Saved.Enqueue(chat);
            Calls.Enqueue("save");
        }
        public void Delete(string scope, Guid id) => Calls.Enqueue("delete");
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

    private sealed class TestSettings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<AppSettings>? Changed;
        public AppSettings Load() => Current;
        public void Save() => Changed?.Invoke(this, Current);
        public void SaveQuietly() { }
        public void Replace(AppSettings settings) { Current = settings; Save(); }
    }

    private sealed class TestTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void FireQueuedTick() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
