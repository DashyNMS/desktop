using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DesktopNMS.Services.Intelligence;

/// <summary>
/// Desktop's <see cref="IIntelligenceTools"/>: the DashyNMS MCP server,
/// bundled beside the app in mcp/ and started as a private
/// child process over stdio, with no console window. It gets only what it
/// needs - the signed-in LibreNMS connection, and Unimus and Graylog where
/// they're set up - through its own environment, never inheriting this
/// process's. ChatGPT's tokens never reach it.
/// </summary>
/// <remarks>
/// Started on first use and kept running; a crashed or exited server is
/// started again on the next call, and a change of LibreNMS connection
/// restarts it so it never answers for the previous network.
/// </remarks>
public sealed class McpToolHost : IIntelligenceTools, IAsyncDisposable
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "dashynms-mcp.exe" : "dashynms-mcp";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IUnimusTokenProtector _unimusToken;
    private readonly IGraylogPasswordProtector _graylogPassword;
    private readonly ILogger<McpToolHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DesktopNMS.Core.Sensors.SensorDefinitionStore? _sensorDefinitions;

    private McpClient? _client;
    private IReadOnlyList<IntelligenceTool>? _tools;
    private Dictionary<string, McpClientTool> _byName = new();
    private string? _startedFor;

    public McpToolHost(
        ISessionService session,
        ISettingsStore settings,
        IUnimusTokenProtector unimusToken,
        IGraylogPasswordProtector graylogPassword,
        ILoggerFactory loggerFactory,
        DesktopNMS.Core.Sensors.SensorDefinitionStore? sensorDefinitions = null)
    {
        _session = session;
        _sensorDefinitions = sensorDefinitions;
        _settings = settings;
        _unimusToken = unimusToken;
        _graylogPassword = graylogPassword;
        _logger = loggerFactory.CreateLogger<McpToolHost>();
    }

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "mcp", ExecutableName);

    public static bool IsBundled => File.Exists(ExecutablePath);

    public async Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            return _tools!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        McpClient client;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            if (!_byName.ContainsKey(name))
            {
                return new IntelligenceToolResult($"There is no tool called \"{name}\".", IsError: true);
            }

            client = _client!;
        }
        finally
        {
            _gate.Release();
        }

        var args = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        try
        {
            var result = await client.CallToolAsync(name, args, cancellationToken: timeout.Token).ConfigureAwait(false);
            var images = result.Content
                .OfType<ImageContentBlock>()
                .Select(i => new IntelligenceImage(i.DecodedData.ToArray(), i.MimeType ?? "image/png"))
                .ToList();
            return new IntelligenceToolResult(Text(result), result.IsError == true, images);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IntelligenceToolResult($"{name} took longer than {CallTimeout.TotalSeconds:0} seconds.", IsError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Intelligence: tool {Tool} failed ({ErrorType})", name, ex.GetType().Name);
            await StopAsync().ConfigureAwait(false);
            return new IntelligenceToolResult("The network tools stopped unexpectedly. They'll start again on the next question.", IsError: true);
        }
    }

    /// <summary>Stops the server - on disconnect, sign-out or exit.</summary>
    public async Task StopAsync()
    {
        var client = _client;
        _client = null;
        _tools = null;
        _byName = new Dictionary<string, McpClientTool>();
        _startedFor = null;
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Intelligence: stopping the network tools ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        var connection = _session.Connection
                         ?? throw new IntelligenceException("Connect DashyNMS to LibreNMS first.", IntelligenceFailure.Network);

        // Another LibreNMS (or a new token) means a new server, so answers
        // can't come from the previous network.
        // A changed config-text choice restarts it too: it's set at startup.
        var identity = connection.WebRoot + "|" + connection.ApiToken.GetHashCode(StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture)
                       + "|" + ConfigText(_settings.Current);
        if (_client is not null && _startedFor == identity)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);

        if (!IsBundled)
        {
            throw new IntelligenceException("DashyNMS's network tools aren't installed with this build.", IntelligenceFailure.Tools);
        }

        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (_sensorDefinitions is not null)
        {
            environment["DASHYNMS_SENSOR_DEFINITIONS_DIRECTORY"] = _sensorDefinitions.Paths.DefinitionsDirectory;
            environment["DASHYNMS_SENSOR_DEFINITIONS_SETTINGS"] = _sensorDefinitions.Paths.SettingsFile;
        }
        foreach (var (key, value) in Configuration(connection))
        {
            environment[key] = value;
        }

        var stderr = new Queue<string>();
        var listening = true;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "DashyNMS network tools",
            Command = ExecutablePath,
            Arguments = Array.Empty<string>(),
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
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

        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            _client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "DashyNMS desktop", Version = typeof(McpToolHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            }, NullLoggerFactory.Instance, startTimeout.Token).ConfigureAwait(false);

            var listed = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _byName = listed
                .Where(t => IntelligenceToolAllowlist.Names.Contains(t.Name))
                .ToDictionary(t => t.Name, StringComparer.Ordinal);
            _tools = _byName.Values
                .Select(t => new IntelligenceTool(t.Name, t.Description ?? string.Empty, t.JsonSchema))
                .ToList();
            _startedFor = identity;
            _logger.LogInformation("Intelligence: network tools started with {Count} of {Listed} tools", _tools.Count, listed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = await StartReasonAsync(stderr).ConfigureAwait(false);
            _logger.LogWarning("Intelligence: the network tools didn't start ({ErrorType}, {Reason})", ex.GetType().Name, reason);
            await StopAsync().ConfigureAwait(false);
            throw new IntelligenceException(
                McpStartReason.Describe(reason) ?? "DashyNMS's network tools couldn't start. Check your connection and Intelligence settings, then try again.",
                IntelligenceFailure.Tools);
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

    /// <summary>The MCP server's settings (see the MCP README's table), from this app's own connection and settings.</summary>
    private IEnumerable<KeyValuePair<string, string>> Configuration(LibreNmsConnection connection)
    {
        var settings = _settings.Current;

        yield return new("LIBRENMS_URL", connection.WebRoot.ToString());
        yield return new("LIBRENMS_TOKEN", connection.ApiToken);
        yield return new("LIBRENMS_TIMEOUT", Math.Max(5, connection.TimeoutSeconds).ToString(CultureInfo.InvariantCulture));
        yield return new("LIBRENMS_TIMESTAMPS_UTC", settings.ServerTimestampsAreUtc ? "true" : "false");
        if (connection.BackupWebRoot is { } backup)
        {
            yield return new("LIBRENMS_BACKUP_URL", backup.ToString());
        }

        if (connection.TrustedCertificates.Count > 0)
        {
            yield return new("LIBRENMS_TRUSTED_CERTS", string.Join(",", connection.TrustedCertificates));
        }

        // Settings › Intelligence: full (redacted) config text, a summary
        // ("did it change, when and by how much"), or nothing from Unimus.
        yield return new("MCP_CONFIG_TEXT", ConfigText(settings));

        if (settings.Unimus is { Enabled: true, Url: { Length: > 0 } unimusUrl } unimus && _unimusToken.Load() is { Length: > 0 } unimusToken)
        {
            yield return new("UNIMUS_URL", unimusUrl);
            yield return new("UNIMUS_TOKEN", unimusToken);
            if (unimus.TrustedCertificates.Count > 0)
            {
                yield return new("UNIMUS_TRUSTED_CERTS", string.Join(",", unimus.TrustedCertificates));
            }
        }

        if (settings.Graylog is { Enabled: true } graylog
            && GraylogConnection.FromSettings(graylog, _graylogPassword.Load(), out _) is { } graylogConnection)
        {
            yield return new("GRAYLOG_URL", graylogConnection.Root.ToString());
            yield return new("GRAYLOG_USERNAME", graylog.Username ?? string.Empty);
            yield return new("GRAYLOG_PASSWORD", _graylogPassword.Load() ?? string.Empty);
            yield return new("GRAYLOG_VERSION", graylog.Version == GraylogSettings.Version20 ? "2.0" : "2.1");
            yield return new("GRAYLOG_QUERY_FIELD", graylog.QueryField);
            yield return new("GRAYLOG_MATCH_ANY_ADDRESS", graylog.MatchAnyAddress ? "true" : "false");
            if (graylog.TrustedCertificates.Count > 0)
            {
                yield return new("GRAYLOG_TRUSTED_CERTS", string.Join(",", graylog.TrustedCertificates));
            }
        }
    }

    /// <summary>The MCP server's MCP_CONFIG_TEXT - summary unless set to one of the others.</summary>
    private static string ConfigText(AppSettings settings) => settings.IntelligenceConfigText switch
    {
        AppSettings.IntelligenceConfigTextFull => "full",
        AppSettings.IntelligenceConfigTextOff => "off",
        _ => "summary",
    };

    /// <summary>The tool's text content, joined - what the model reads.</summary>
    private static string Text(CallToolResult result)
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
