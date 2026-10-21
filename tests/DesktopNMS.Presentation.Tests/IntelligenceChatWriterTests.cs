using System.Collections.Concurrent;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Saved chats written off the UI thread, in order (#321).</summary>
public class IntelligenceChatWriterTests
{
    [Fact]
    public void Saves_are_written_off_the_calling_thread()
    {
        var store = new RecordingStore();
        using var writer = new IntelligenceChatWriter(store, NullLogger.Instance);

        writer.Save("scope", new SavedIntelligenceChat { Title = "one" });
        writer.Flush();

        var write = Assert.Single(store.Writes);
        Assert.NotEqual(Environment.CurrentManagedThreadId, write.Thread);
    }

    [Fact]
    public void A_waiting_save_is_replaced_by_a_newer_one_of_the_same_chat()
    {
        var store = new RecordingStore { Gate = new ManualResetEventSlim(false) };
        using var writer = new IntelligenceChatWriter(store, NullLogger.Instance);
        var chat = Guid.NewGuid();

        // The first write blocks; the next three wait, and only the last of them is written.
        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "1" });
        SpinWait.SpinUntil(() => store.Started > 0, TimeSpan.FromSeconds(5));
        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "2" });
        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "3" });
        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "4" });
        store.Gate.Set();
        writer.Flush();

        Assert.Equal(new[] { "save 1", "save 4" }, store.Writes.Select(w => w.What));
    }

    [Fact]
    public void A_delete_follows_the_saves_before_it_and_drops_waiting_ones()
    {
        var store = new RecordingStore { Gate = new ManualResetEventSlim(false) };
        using var writer = new IntelligenceChatWriter(store, NullLogger.Instance);
        var chat = Guid.NewGuid();

        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "1" });
        SpinWait.SpinUntil(() => store.Started > 0, TimeSpan.FromSeconds(5));
        writer.Save("scope", new SavedIntelligenceChat { Id = chat, Title = "2" });
        writer.Delete("scope", chat);
        store.Gate.Set();
        writer.Flush();

        Assert.Equal(new[] { "save 1", "delete" }, store.Writes.Select(w => w.What));
    }

    [Fact]
    public void A_failed_write_is_reported_and_the_rest_carry_on()
    {
        var store = new RecordingStore { FailTitle = "bad" };
        using var writer = new IntelligenceChatWriter(store, NullLogger.Instance);
        var failures = new ConcurrentBag<string>();
        writer.Failed += failures.Add;

        writer.Save("scope", new SavedIntelligenceChat { Title = "bad" });
        writer.Save("scope", new SavedIntelligenceChat { Title = "good" });
        writer.Flush();

        Assert.Contains("Couldn't save this chat", Assert.Single(failures), StringComparison.Ordinal);
        Assert.Contains(store.Writes, w => w.What == "save good");
    }

    [Fact]
    public void Disposing_writes_everything_still_waiting()
    {
        var store = new RecordingStore();
        var writer = new IntelligenceChatWriter(store, NullLogger.Instance);

        for (var i = 0; i < 20; i++)
        {
            writer.Save("scope", new SavedIntelligenceChat { Title = $"{i}" });
        }

        writer.Dispose();

        Assert.Equal(20, store.Writes.Count);
    }

    private sealed class RecordingStore : IIntelligenceChatStore
    {
        public ManualResetEventSlim? Gate { get; init; }

        public string? FailTitle { get; init; }

        public ConcurrentQueue<(string What, int Thread)> Writes { get; } = new();

        public int Started;

        public SavedChatCollection Load(string scope) => new(null, Array.Empty<SavedIntelligenceChat>());

        public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true)
        {
            Interlocked.Increment(ref Started);
            Gate?.Wait(TimeSpan.FromSeconds(5));
            if (chat.Title == FailTitle)
            {
                throw new IOException("disk full");
            }

            Writes.Enqueue(("save " + chat.Title, Environment.CurrentManagedThreadId));
        }

        public void Delete(string scope, Guid id) => Writes.Enqueue(("delete", Environment.CurrentManagedThreadId));
    }
}
