using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DesktopNMS.Services.Intelligence;

/// <summary>One started network-tools server - a generation - as <see cref="McpToolHost"/> uses it.</summary>
internal interface IMcpToolProcess : IAsyncDisposable
{
    /// <summary>The version the server reported when it started.</summary>
    string? Version { get; }

    /// <summary>Completes when the server exits.</summary>
    Task Completion { get; }

    /// <summary>The allowlisted tools it offered.</summary>
    IReadOnlyList<IntelligenceTool> Tools { get; }

    Task<IntelligenceToolResult> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

/// <summary>Starts a server with exactly these settings in its environment, or throws <see cref="IntelligenceException"/>.</summary>
internal delegate Task<IMcpToolProcess> McpToolProcessStart(IReadOnlyList<KeyValuePair<string, string>> settings, CancellationToken cancellationToken);

/// <summary>
/// The bundled server as a private child process over stdio, with no console
/// window and none of this process's environment.
/// </summary>
internal sealed class StdioMcpToolProcess : IMcpToolProcess
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly McpClient _client;

    private StdioMcpToolProcess(McpClient client, IReadOnlyList<IntelligenceTool> tools)
    {
        _client = client;
        Tools = tools;
    }

    public string? Version => _client.ServerInfo?.Version;
    public Task Completion => _client.Completion;
    public IReadOnlyList<IntelligenceTool> Tools { get; }

    public static async Task<IMcpToolProcess> StartAsync(string executable, IReadOnlyList<KeyValuePair<string, string>> settings, ILogger logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            throw new IntelligenceException("DashyNMS's network tools aren't installed with this build.", IntelligenceFailure.Tools);
        }

        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (key, value) in settings)
        {
            environment[key] = value;
        }

        var stderr = new Queue<string>();
        var listening = true;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "DashyNMS network tools",
            Command = executable,
            Arguments = Array.Empty<string>(),
            WorkingDirectory = Path.GetDirectoryName(executable),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),

            // Stderr and protocol diagnostics may contain credentials or tool data.
            // Keep them out of the UI, persistent logs and debug output: the
            // last few lines stay in memory only, for McpStartReason (#325).
            StandardErrorLines = line =>
            {
                lock (stderr)
                {
                    if (!listening) return;
                    if (stderr.Count == McpStartReason.MaxLines) stderr.Dequeue();
                    stderr.Enqueue(line);
                }
            },
        }, NullLoggerFactory.Instance);

        McpClient? client = null;
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(StartTimeout);
            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "DashyNMS desktop", Version = typeof(McpToolHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            }, NullLoggerFactory.Instance, startTimeout.Token).ConfigureAwait(false);

            var listed = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var tools = listed
                .Where(t => IntelligenceToolAllowlist.Names.Contains(t.Name))
                .Select(t => new IntelligenceTool(t.Name, t.Description ?? string.Empty, t.JsonSchema))
                .ToList();
            logger.LogInformation("Intelligence: network tools started with {Count} of {Listed} tools", tools.Count, listed.Count);
            return new StdioMcpToolProcess(client, tools);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = await StartReasonAsync(stderr).ConfigureAwait(false);
            logger.LogWarning("Intelligence: the network tools didn't start ({ErrorType}, {Reason})", ex.GetType().Name, reason);
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            throw new IntelligenceException(
                McpStartReason.Describe(reason) ?? "DashyNMS's network tools couldn't start. Check your connection and Intelligence settings, then try again.",
                IntelligenceFailure.Tools);
        }
        catch
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Only a start's lines are kept; nothing once it's running.
            lock (stderr)
            {
                listening = false;
                stderr.Clear();
            }
        }
    }

    public async Task<IntelligenceToolResult> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var result = await _client.CallToolAsync(name, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        var images = result.Content
            .OfType<ImageContentBlock>()
            .Select(i => new IntelligenceImage(i.DecodedData.ToArray(), i.MimeType ?? "image/png"))
            .ToList();
        return new IntelligenceToolResult(Text(result), result.IsError == true, images);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static async Task DisposeQuietlyAsync(McpClient? client)
    {
        if (client is null) return;
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already failing to start; the start's own failure is what's reported.
        }
    }

    /// <summary>
    /// The fixed reason word for a failed start. The server's last line can
    /// arrive just after the exit is noticed, so an unknown reason waits a
    /// moment and looks again.
    /// </summary>
    private static async Task<string> StartReasonAsync(Queue<string> stderr)
    {
        string Classify()
        {
            lock (stderr) return McpStartReason.Classify(stderr.ToList());
        }

        var reason = Classify();
        if (reason != McpStartReason.Unknown) return reason;
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        return Classify();
    }

    /// <summary>The tool's text content, joined - what the model reads.</summary>
    internal static string Text(CallToolResult result)
    {
        var text = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock t)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(t.Text);
            }
        }

        if (text.Length == 0 && result.StructuredContent is { } structured)
        {
            text.Append(structured.ToString());
        }

        return text.Length == 0 ? "{}" : text.ToString();
    }
}
