using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>A JSON-RPC error the agent returned for a request.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC 2.0 over an <see cref="AgentProcess"/>'s stdio, one message per
/// line or Content-Length frame (#332): requests matched to their answers, notifications and the
/// agent's own requests handed to the owner as they arrive, in order.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly AgentProcess _process;
    private readonly Func<string, JsonNode?, Task> _onNotification;
    private readonly Func<string, JsonNode?, Task<JsonNode?>> _onRequest;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reading;
    private long _nextId;

    /// <param name="onNotification">Each notification, awaited before the next message is read so events keep their order.</param>
    /// <param name="onRequest">Each request from the agent; its result is the answer. Throw <see cref="JsonRpcException"/> to refuse.</param>
    public JsonRpcConnection(AgentProcess process, Func<string, JsonNode?, Task> onNotification, Func<string, JsonNode?, Task<JsonNode?>> onRequest)
    {
        _process = process;
        _onNotification = onNotification;
        _onRequest = onRequest;
        _reading = ReadAsync();
    }

    /// <summary>Completes when the agent's output ends - it exited or was killed.</summary>
    public Task Completion => _reading;

    public bool IsClosed => _reading.IsCompleted;

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (IsClosed) throw new AgentProviderException("The agent stopped unexpectedly.", Core.Intelligence.IntelligenceFailure.Model);
            // Always present, if only as null: some servers refuse a request without it.
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            await _process.WriteLineAsync(message.ToJsonString(), cancellationToken).ConfigureAwait(false);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return _process.WriteLineAsync(message.ToJsonString(), cancellationToken);
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var line in _process.ReadLinesAsync(_closing.Token).ConfigureAwait(false))
            {
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    // Not protocol output: a stray log line is skipped rather than ending the session.
                    continue;
                }

                if (message is null) continue;
                var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
                var id = message["id"];
                if (method is null)
                {
                    if (id is JsonValue v && v.TryGetValue<long>(out var answerId) && _pending.TryGetValue(answerId, out var pending))
                    {
                        if (message["error"] is JsonObject error)
                        {
                            var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var number) ? number : 0;
                            pending.TrySetException(new JsonRpcException(code, error["message"]?.ToString() ?? "The agent refused the request."));
                        }
                        else
                        {
                            pending.TrySetResult(message["result"]?.DeepClone());
                        }
                    }

                    continue;
                }

                if (id is null)
                {
                    await _onNotification(method, message["params"]).ConfigureAwait(false);
                    continue;
                }

                // The agent's own request - an approval, say. Answered off the read loop so a slow
                // answer never holds up the events behind it.
                var requestId = id.DeepClone();
                var parameters = message["params"]?.DeepClone();
                _ = AnswerAsync(requestId, method, parameters);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new AgentProviderException("The agent stopped unexpectedly.", Core.Intelligence.IntelligenceFailure.Model));
            }
        }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        JsonObject reply;
        try
        {
            var result = await _onRequest(method, parameters).ConfigureAwait(false);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (JsonRpcException ex)
        {
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } };
        }

        try
        {
            await _process.WriteLineAsync(reply.ToJsonString(), _closing.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The agent has gone; nobody is waiting for the answer.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _reading.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
