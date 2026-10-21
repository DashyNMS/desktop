using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services.Intelligence;

/// <summary>
/// Writes saved Intelligence chats off the UI thread (#321): serialising a
/// long chat with graphs, encrypting and writing it is several MB of work,
/// which stuttered the window when it ran there while an answer streamed.
/// </summary>
/// <remarks>
/// One writer, in order, so a delete never races a save of the same chat. A
/// save still waiting is replaced by a newer one of the same chat, and a
/// delete drops any waiting, so a busy chat is written as often as the disk
/// keeps up, never queued without end. Callers pass a snapshot the UI thread
/// won't change afterwards.
/// </remarks>
internal sealed class IntelligenceChatWriter : IDisposable
{
    private readonly IIntelligenceChatStore _store;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly LinkedList<Operation> _pending = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _disposed;

    public IntelligenceChatWriter(IIntelligenceChatStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Raised, on the writer's thread, with a line for the user when a write fails.</summary>
    public event Action<string>? Failed;

    public void Save(string scope, SavedIntelligenceChat snapshot, bool makeActive = true)
        => Enqueue(new Operation(scope, snapshot.Id, IsSave: true, store => store.Save(scope, snapshot, makeActive),
            "Couldn't save this chat. Check that your DashyNMS data folder is writable."));

    public void Delete(string scope, Guid id)
        => Enqueue(new Operation(scope, id, IsSave: false, store => store.Delete(scope, id), "Couldn't delete the saved chat."));

    /// <summary>Waits for everything queued so far to be written - before reading the chats back, and on exit.</summary>
    public void Flush()
    {
        while (true)
        {
            Task worker;
            lock (_sync)
            {
                if (!_running && _pending.Count == 0)
                {
                    return;
                }

                worker = _worker;
            }

            if (!worker.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("Intelligence: saved chats were still being written after 30 seconds");
                return;
            }
        }
    }

    /// <summary>Writes whatever is waiting; anything after this is written straight away.</summary>
    public void Dispose()
    {
        Flush();
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void Enqueue(Operation operation)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                for (var node = _pending.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.Scope == operation.Scope && node.Value.ChatId == operation.ChatId && node.Value.IsSave)
                    {
                        _pending.Remove(node);
                    }

                    node = next;
                }

                _pending.AddLast(operation);
                if (!_running)
                {
                    _running = true;
                    _worker = Task.Run(Drain);
                }

                return;
            }
        }

        // Closing down: nothing will drain the queue any more, so write now.
        Run(operation);
    }

    private void Drain()
    {
        while (true)
        {
            Operation operation;
            lock (_sync)
            {
                if (_pending.First is not { } first)
                {
                    _running = false;
                    return;
                }

                operation = first.Value;
                _pending.RemoveFirst();
            }

            Run(operation);
        }
    }

    private void Run(Operation operation)
    {
        try
        {
            operation.Write(_store);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intelligence: a saved chat could not be {Action}", operation.IsSave ? "saved" : "deleted");
            Failed?.Invoke(operation.FailureText);
        }
    }

    private sealed record Operation(string Scope, Guid ChatId, bool IsSave, Action<IIntelligenceChatStore> Write, string FailureText);
}
