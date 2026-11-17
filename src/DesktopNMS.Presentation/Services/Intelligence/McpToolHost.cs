using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;

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
/// Each started server is a generation, tied to the exact settings it was
/// given. Started on first use and kept running; a crashed, exited or
/// unresponsive server is replaced on the next call. Signing out, switching
/// LibreNMS or saving a setting it uses retires the generation at once, and
/// anything it was still looking up is discarded, so answers never come from
/// the previous network, account or settings.
/// </remarks>
public sealed partial class McpToolHost : IIntelligenceTools, IAsyncDisposable
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "dashynms-mcp.exe" : "dashynms-mcp";

    internal const string Discarded = "The LibreNMS connection or Intelligence settings changed during this lookup, so its result was discarded.";

    internal const string Interrupted = "The network tools stopped during this lookup, so its result was discarded. They'll start again on the next lookup.";

    private readonly TimeSpan _callTimeout;

    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IUnimusTokenProtector _unimusToken;
    private readonly IGraylogPasswordProtector _graylogPassword;
    private readonly ILogger<McpToolHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DesktopNMS.Core.Sensors.SensorDefinitionStore? _sensorDefinitions;
    private readonly McpToolProcessStart _start;

    private Generation? _current;
    private CancellationTokenSource? _starting;

    // Counted, so a start that finishes after a stop is thrown away rather than kept.
    private long _stops;

    public event EventHandler? VersionChanged;
    public string? ReportedVersion => Volatile.Read(ref _current) is { } current && !current.Process.Completion.IsCompleted
        ? ComponentVersions.Clean(current.Process.Version) : null;

    public McpToolHost(
        ISessionService session,
        ISettingsStore settings,
        IUnimusTokenProtector unimusToken,
        IGraylogPasswordProtector graylogPassword,
        ILoggerFactory loggerFactory,
        DesktopNMS.Core.Sensors.SensorDefinitionStore? sensorDefinitions = null)
        : this(session, settings, unimusToken, graylogPassword, loggerFactory, sensorDefinitions, start: null, callTimeout: null)
    {
    }

    internal McpToolHost(
        ISessionService session,
        ISettingsStore settings,
        IUnimusTokenProtector unimusToken,
        IGraylogPasswordProtector graylogPassword,
        ILoggerFactory loggerFactory,
        DesktopNMS.Core.Sensors.SensorDefinitionStore? sensorDefinitions,
        McpToolProcessStart? start,
        TimeSpan? callTimeout)
    {
        _session = session;
        _sensorDefinitions = sensorDefinitions;
        _settings = settings;
        _unimusToken = unimusToken;
        _graylogPassword = graylogPassword;
        _logger = loggerFactory.CreateLogger<McpToolHost>();
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(90);
        _start = start ?? ((values, token) => StdioMcpToolProcess.StartAsync(ExecutablePath, values, _logger, token));
        _session.StateChanged += OnSourcesChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "mcp", ExecutableName);

    public static bool IsBundled => File.Exists(ExecutablePath);

    public async Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken)
        => (await AcquireAsync(cancellationToken).ConfigureAwait(false)).Process.Tools;

    public async Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var generation = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!generation.Process.Tools.Any(t => t.Name == name))
        {
            return new IntelligenceToolResult($"There is no tool called \"{name}\".", IsError: true);
        }

        var args = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation.Retired);
        call.CancelAfter(_callTimeout);

        IntelligenceToolResult result;
        try
        {
            result = await generation.Process.CallAsync(name, args, call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (generation.Retired.IsCancellationRequested) return new IntelligenceToolResult(generation.Reason, IsError: true);

            // An unresponsive server isn't asked again; the next lookup starts a fresh one.
            _logger.LogWarning("Intelligence: tool {Tool} timed out; the network tools will restart", name);
            await RetireAsync(generation, Interrupted).ConfigureAwait(false);
            return new IntelligenceToolResult($"{name} took longer than {_callTimeout.TotalSeconds:0} seconds.", IsError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation.Retired.IsCancellationRequested) return new IntelligenceToolResult(generation.Reason, IsError: true);
            _logger.LogWarning("Intelligence: tool {Tool} failed ({ErrorType})", name, ex.GetType().Name);

            // This generation only: a newer one may already be answering.
            await RetireAsync(generation, Interrupted).ConfigureAwait(false);
            return new IntelligenceToolResult("The network tools stopped unexpectedly. They'll start again on the next question.", IsError: true);
        }

        // A result arriving after a sign-out, switch or saved change answers for the old ones.
        if (IsCurrent(generation)) return result;
        return new IntelligenceToolResult(generation.Retired.IsCancellationRequested ? generation.Reason : Discarded, IsError: true);
    }

    /// <summary>Stops the server - on disconnect, sign-out or exit - including one still starting.</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _stops);
        try
        {
            Volatile.Read(ref _starting)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That start has already finished; the stop count discards it.
        }

        if (Volatile.Read(ref _current) is { } current)
        {
            await RetireAsync(current, Discarded).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.StateChanged -= OnSourcesChanged;
        _settings.Changed -= OnSettingsChanged;
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>The running generation for the current settings, started if needed.</summary>
    private async Task<Generation> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A switch or saved change during a start retires what started; the
            // next attempt starts for the new settings, or reports the sign-out.
            for (var attempt = 1; ; attempt++)
            {
                if (await AcquireOnceAsync(cancellationToken).ConfigureAwait(false) is { } generation) return generation;
                if (attempt == 3) throw StoppedWhileStarting();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One attempt, under the gate: the generation, or null when it no longer matched once started.</summary>
    private async Task<Generation?> AcquireOnceAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> settings;
        try
        {
            settings = CurrentSettings()
                       ?? throw new IntelligenceException("Connect DashyNMS to LibreNMS first.", IntelligenceFailure.Network);
        }
        catch (IntelligenceException)
        {
            // Signed out, or settings it can't use: nothing keeps the old server running.
            await StopAsync().ConfigureAwait(false);
            throw;
        }

        var identity = Identity(settings);
        if (Volatile.Read(ref _current) is { } current)
        {
            if (current.Identity == identity && !current.Process.Completion.IsCompleted) return current;
            await RetireAsync(current, Discarded).ConfigureAwait(false);
        }

        var stops = Interlocked.Read(ref _stops);
        IMcpToolProcess process;
        using (var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Volatile.Write(ref _starting, starting);
            try
            {
                process = await _start(settings, starting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw StoppedWhileStarting();
            }
            finally
            {
                Volatile.Write(ref _starting, null);
            }
        }

        var generation = new Generation(process, identity);
        Volatile.Write(ref _current, generation);

        // A stop while it started - Intelligence turned off, exit - wins.
        if (Interlocked.Read(ref _stops) != stops)
        {
            await RetireAsync(generation, Discarded).ConfigureAwait(false);
            throw StoppedWhileStarting();
        }

        // A sign-out, switch or saved change while it started: it holds the old
        // settings, so it never keeps running, even with nothing else to retire it.
        if (!IsCurrent(generation))
        {
            await RetireAsync(generation, Discarded).ConfigureAwait(false);
            return null;
        }

        NotifyVersionChanged();
        _ = ObserveCompletionAsync(generation);
        return generation;
    }

    private static IntelligenceException StoppedWhileStarting()
        => new("The network tools were stopped while they started. Ask again to start them.", IntelligenceFailure.Tools);

    /// <summary>The server's settings now, or null when signed out.</summary>
    private List<KeyValuePair<string, string>>? CurrentSettings()
    {
        if (_session.Connection is not { } connection) return null;
        var settings = ToolSettings(connection, _settings.Current, _unimusToken.Load, _graylogPassword.Load);
        if (_sensorDefinitions is not null)
        {
            settings.Add(new("DASHYNMS_SENSOR_DEFINITIONS_DIRECTORY", _sensorDefinitions.Paths.DefinitionsDirectory));
            settings.Add(new("DASHYNMS_SENSOR_DEFINITIONS_SETTINGS", _sensorDefinitions.Paths.SettingsFile));
        }

        return settings;
    }

    private bool IsCurrent(Generation generation)
    {
        if (!ReferenceEquals(Volatile.Read(ref _current), generation) || generation.Retired.IsCancellationRequested) return false;
        try
        {
            return CurrentSettings() is { } settings && Identity(settings) == generation.Identity;
        }
        catch (IntelligenceException)
        {
            return false;
        }
    }

    private void OnSourcesChanged(object? sender, EventArgs e) => _ = RetireIfOutdatedAsync();

    private void OnSettingsChanged(object? sender, AppSettings e) => _ = RetireIfOutdatedAsync();

    /// <summary>Retires the running generation once the connection or settings no longer match it.</summary>
    private async Task RetireIfOutdatedAsync()
    {
        try
        {
            if (Volatile.Read(ref _current) is { } current && !IsCurrent(current))
            {
                await RetireAsync(current, Discarded).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Intelligence: retiring the network tools ({ErrorType})", ex.GetType().Name);
        }
    }

    /// <summary>Cancels a generation's lookups and stops its server, leaving any newer one alone.</summary>
    /// <remarks>Every caller waits for the same stop, so exit never moves on while a server is still stopping.</remarks>
    private async Task RetireAsync(Generation generation, string reason)
    {
        var wasCurrent = ReferenceEquals(Interlocked.CompareExchange(ref _current, null, generation), generation);
        await generation.RetireAsync(reason, StopProcessAsync).ConfigureAwait(false);
        if (wasCurrent) NotifyVersionChanged();

        async Task StopProcessAsync()
        {
            try
            {
                await generation.Process.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Intelligence: stopping the network tools ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    private void NotifyVersionChanged()
    {
        try
        {
            VersionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A listener's failure never stops a server being retired or kept.
            _logger.LogDebug("Intelligence: a version listener failed ({ErrorType})", ex.GetType().Name);
        }
    }

    private async Task ObserveCompletionAsync(Generation generation)
    {
        try
        {
            await generation.Process.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // How it ended doesn't matter here, only that it did.
        }

        if (ReferenceEquals(Volatile.Read(ref _current), generation)) NotifyVersionChanged();
    }

    /// <summary>One started server and the exact settings it was started with.</summary>
    private sealed class Generation(IMcpToolProcess process, string identity)
    {
        private readonly CancellationTokenSource _retired = new();
        private readonly object _sync = new();
        private Task? _stopped;

        public IMcpToolProcess Process { get; } = process;
        public string Identity { get; } = identity;

        /// <summary>Cancelled when retired, ending its lookups.</summary>
        public CancellationToken Retired => _retired.Token;

        /// <summary>What its unfinished lookups are told: superseded, or the server itself stopped.</summary>
        public string Reason { get; private set; } = Discarded;

        /// <summary>Retires it once; later callers wait for that same stop.</summary>
        public Task RetireAsync(string reason, Func<Task> stop)
        {
            lock (_sync)
            {
                if (_stopped is null)
                {
                    Reason = reason;
                    _retired.Cancel();
                    _stopped = stop();
                }

                return _stopped;
            }
        }
    }
}
