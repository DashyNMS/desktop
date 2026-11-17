using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Each started server answers only for the connection, account and settings it was started with.</summary>
public sealed class McpToolHostGenerationTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;
    private static LibreNmsConnection Network(string token, string url = "https://a.example/") => new(new Uri(url), token);

    [Fact]
    public async Task Signing_out_stops_the_server_at_once_and_discards_a_lookup_still_running()
    {
        var harness = new Harness(Network("token-a"));
        var pending = harness.HoldNextCall();
        var lookup = harness.Host.CallAsync("find_device", NoArguments, CancellationToken.None);
        await harness.CallStarted;

        harness.Session.Connection = null;
        harness.Session.Raise();
        pending.SetResult(new IntelligenceToolResult("{\"status\":\"ok\",\"data\":\"from account A\"}", false));

        var result = await lookup;
        Assert.True(result.IsError);
        Assert.Equal(McpToolHost.Discarded, result.Output);
        Assert.True(harness.Started.Single().Disposed);
        var failure = await Assert.ThrowsAsync<IntelligenceException>(() => harness.Host.GetToolsAsync(CancellationToken.None));
        Assert.Equal(IntelligenceFailure.Network, failure.Failure);
        Assert.Single(harness.Started);
    }

    [Fact]
    public async Task The_same_address_with_another_token_and_A_to_B_to_A_each_get_a_fresh_server()
    {
        var harness = new Harness(Network("token-a"));
        await harness.Host.GetToolsAsync(CancellationToken.None);
        harness.Session.Connection = Network("token-b");
        harness.Session.Raise();
        await harness.Host.GetToolsAsync(CancellationToken.None);
        harness.Session.Connection = Network("token-a", "https://b.example/");
        await harness.Host.GetToolsAsync(CancellationToken.None);
        harness.Session.Connection = Network("token-a");
        await harness.Host.GetToolsAsync(CancellationToken.None);

        Assert.Equal(new[] { "token-a", "token-b", "token-a", "token-a" }, harness.Started.Select(p => p.Value("LIBRENMS_TOKEN")));
        Assert.Equal("https://b.example/", harness.Started[2].Value("LIBRENMS_URL"));
        Assert.All(harness.Started.SkipLast(1), p => Assert.True(p.Disposed));
        Assert.False(harness.Started[^1].Disposed);
    }

    [Fact]
    public async Task A_saved_threshold_discards_the_old_settings_lookup_and_the_next_one_uses_the_new_value()
    {
        var harness = new Harness(Network("token-a"));
        var pending = harness.HoldNextCall();
        var lookup = harness.Host.CallAsync("device_sensors", NoArguments, CancellationToken.None);
        await harness.CallStarted;

        harness.Settings.Current.TemperatureThresholds.HighWarning = 40;
        harness.Settings.Save();
        pending.TrySetResult(new IntelligenceToolResult("{\"status\":\"ok\",\"data\":\"judged at 60\"}", false));

        Assert.Equal(McpToolHost.Discarded, (await lookup).Output);
        await harness.Host.CallAsync("device_sensors", NoArguments, CancellationToken.None);
        Assert.Equal(new[] { "60", "40" }, harness.Started.Select(p => p.Value("MCP_TEMPERATURE_HIGH_WARNING")));
        Assert.True(harness.Started[0].Disposed);
    }

    [Fact]
    public async Task Saving_settings_the_tools_do_not_use_keeps_the_running_server()
    {
        var harness = new Harness(Network("token-a"));
        await harness.Host.GetToolsAsync(CancellationToken.None);

        harness.Settings.Current.IntelligenceModel = "another-model";
        harness.Settings.Save();
        await harness.Host.CallAsync("find_device", NoArguments, CancellationToken.None);

        Assert.Single(harness.Started);
        Assert.False(harness.Started[0].Disposed);
    }

    [Fact]
    public async Task An_old_server_s_failing_lookup_never_stops_the_newer_server()
    {
        var harness = new Harness(Network("token-a"));
        var pending = harness.HoldNextCall();
        var lookup = harness.Host.CallAsync("find_device", NoArguments, CancellationToken.None);
        await harness.CallStarted;

        // Switched without an event: the next lookup notices and starts B.
        harness.Session.Connection = Network("token-b");
        await harness.Host.GetToolsAsync(CancellationToken.None);
        pending.TrySetException(new IOException("pipe closed"));

        Assert.Equal(McpToolHost.Discarded, (await lookup).Output);
        Assert.Equal(2, harness.Started.Count);
        Assert.False(harness.Started[1].Disposed);
        Assert.False((await harness.Host.CallAsync("find_device", NoArguments, CancellationToken.None)).IsError);
        Assert.Equal(2, harness.Started.Count);
    }

    [Fact]
    public async Task A_failing_lookup_retires_only_its_own_server_and_the_next_lookup_starts_again()
    {
        var harness = new Harness(Network("token-a"));
        var pending = harness.HoldNextCall();
        var lookup = harness.Host.CallAsync("find_device", NoArguments, CancellationToken.None);
        await harness.CallStarted;
        pending.TrySetException(new IOException("pipe closed"));

        var result = await lookup;
        Assert.True(result.IsError);
        Assert.DoesNotContain("pipe", result.Output, StringComparison.Ordinal);
        Assert.True(harness.Started[0].Disposed);
        await harness.Host.GetToolsAsync(CancellationToken.None);
        Assert.Equal(2, harness.Started.Count);
    }

    [Fact]
    public async Task A_stop_while_the_server_starts_discards_it_once_it_has_started()
    {
        var harness = new Harness(Network("token-a"));
        var release = harness.HoldNextStart(ignoreCancellation: true);
        var starting = harness.Host.GetToolsAsync(CancellationToken.None);
        await harness.StartRequested;

        await harness.Host.StopAsync();
        release.SetResult();

        var failure = await Assert.ThrowsAsync<IntelligenceException>(() => starting);
        Assert.Equal(IntelligenceFailure.Tools, failure.Failure);
        Assert.True(harness.Started.Single().Disposed);
        Assert.Null(harness.Host.ReportedVersion);
    }

    [Fact]
    public async Task A_stop_cancels_a_start_that_honours_cancellation()
    {
        var harness = new Harness(Network("token-a"));
        harness.HoldNextStart(ignoreCancellation: false);
        var starting = harness.Host.GetToolsAsync(CancellationToken.None);
        await harness.StartRequested;

        await harness.Host.StopAsync();

        await Assert.ThrowsAsync<IntelligenceException>(() => starting);
        Assert.Empty(harness.Started);
    }

    [Fact]
    public async Task Cancelling_a_question_ends_its_lookup_without_retiring_the_server()
    {
        var harness = new Harness(Network("token-a"));
        harness.HoldNextCall();
        using var question = new CancellationTokenSource();
        var lookup = harness.Host.CallAsync("find_device", NoArguments, question.Token);
        await harness.CallStarted;

        question.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.False(harness.Started.Single().Disposed);
    }

    [Fact]
    public async Task An_exited_server_is_replaced_on_the_next_lookup()
    {
        var harness = new Harness(Network("token-a"));
        await harness.Host.GetToolsAsync(CancellationToken.None);
        harness.Started[0].Exit();

        await harness.Host.GetToolsAsync(CancellationToken.None);

        Assert.Equal(2, harness.Started.Count);
    }

    private sealed class Harness
    {
        private TaskCompletionSource<IntelligenceToolResult>? _heldCall;
        private TaskCompletionSource? _heldStart;
        private bool _startIgnoresCancellation;
        private readonly TaskCompletionSource _callStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(LibreNmsConnection connection)
        {
            Session = new Session { Connection = connection };
            Host = new McpToolHost(Session, Settings, new NoSecret(), new NoSecret(), NullLoggerFactory.Instance, null, StartAsync);
        }

        public Session Session { get; }
        public TestSettings Settings { get; } = new();
        public McpToolHost Host { get; }
        public List<FakeProcess> Started { get; } = new();
        public Task CallStarted => _callStarted.Task;
        public Task StartRequested => _startRequested.Task;

        public TaskCompletionSource<IntelligenceToolResult> HoldNextCall()
            => _heldCall = new TaskCompletionSource<IntelligenceToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HoldNextStart(bool ignoreCancellation)
        {
            _startIgnoresCancellation = ignoreCancellation;
            return _heldStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task<IMcpToolProcess> StartAsync(IReadOnlyList<KeyValuePair<string, string>> settings, CancellationToken cancellationToken)
        {
            if (_heldStart is { } held)
            {
                _heldStart = null;
                _startRequested.TrySetResult();
                if (_startIgnoresCancellation) await held.Task;
                else await held.Task.WaitAsync(cancellationToken);
            }

            var process = new FakeProcess(settings, this);
            Started.Add(process);
            return process;
        }

        public async Task<IntelligenceToolResult> CallAsync(CancellationToken cancellationToken)
        {
            if (_heldCall is not { } held) return new IntelligenceToolResult("{\"status\":\"ok\"}", false);
            _heldCall = null;
            _callStarted.TrySetResult();

            // Like a transport that may ignore cancellation: the result can still arrive later.
            var cancelled = new TaskCompletionSource<IntelligenceToolResult>();
            await using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
            return await await Task.WhenAny(held.Task, cancelled.Task);
        }
    }

    private sealed class FakeProcess(IReadOnlyList<KeyValuePair<string, string>> settings, Harness harness) : IMcpToolProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }
        public string? Version => "1.1.0";
        public Task Completion => _exited.Task;
        public IReadOnlyList<IntelligenceTool> Tools { get; } = new[] { "find_device", "device_sensors" }
            .Select(name => new IntelligenceTool(name, name, JsonDocument.Parse("{\"type\":\"object\"}").RootElement)).ToList();

        public string? Value(string key) => settings.LastOrDefault(p => p.Key == key).Value;

        public void Exit() => _exited.TrySetResult();

        public Task<IntelligenceToolResult> CallAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
            => harness.CallAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Exit();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session : ISessionService
    {
        public LibreNmsConnection? Connection { get; set; }
        public SystemInfo? ServerInfo => null;
        public bool IsConnected => Connection is not null;
        public event EventHandler? StateChanged;
        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
        public Task<ConnectionTestResult> SignInAsync(string serverUrl, string apiToken, bool allowUntrustedCertificate, bool rememberToken, CancellationToken cancellationToken = default, string? backupAddress = null) => throw new NotSupportedException();
        public Task<ConnectionTestResult> ReconnectAsync(string serverUrl, string? newApiToken, bool allowUntrustedCertificate, string? backupAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void TrustCertificate(CertificateDetails certificate) => throw new NotSupportedException();
        public Task<ConnectionTestResult?> TryRestoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SignOut(bool forgetToken) => throw new NotSupportedException();
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

    private sealed class NoSecret : IUnimusTokenProtector, IGraylogPasswordProtector
    {
        public bool HasStoredToken => false;
        public bool HasStoredPassword => false;
        public string? Load() => null;
        public void Save(string value) { }
        public void Clear() { }
    }
}
