using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// Spotlight alert and fault identities, rechecked over real HTTP through Core's
/// route selection and parsing, against a server that answers like LibreNMS:
/// without an explicit state, alert routes return active alerts only.
/// </summary>
public sealed class IntelligenceSpotlightAlertTests
{
    internal sealed record Row(int Id, int DeviceId, int RuleId, int State, string? EntityKey = null);

    public static TheoryData<bool, int> EveryState => new()
    {
        { false, 0 }, { false, 1 }, { false, 2 },
        { true, 0 }, { true, 1 }, { true, 2 },
    };

    [Theory]
    [MemberData(nameof(EveryState))]
    public async Task Alerts_and_faults_stay_valid_whether_current_acknowledged_or_recovered(bool faults, int state)
    {
        using var server = new AlertServer(faults, new Row(73, 12, 4, state, faults ? "port:5" : null));
        var target = IntelligenceObjectTargets.Create(new(faults ? "fault" : "alert", 73, 12, 4), "Alert 73")!;

        var resolved = await server.ResolveAsync(target);

        Assert.NotNull(resolved);
        Assert.Equal(target.Identity, resolved!.Identity);
        Assert.Contains(server.Requests, r => Uri.UnescapeDataString(r) == (faults ? "/api/v0/faults/73" : "/api/v0/alerts/73") + "?state=0,1,2");
    }

    public static TheoryData<string, string, int, int?> Mismatches => new()
    {
        { "wrong parent device", "alert", 73, 4 },
        { "wrong rule", "alert", 73, 9 },
        { "missing object", "alert", 74, 4 },
        { "legacy alert asked for as a fault", "fault", 73, 4 },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task A_mismatched_or_missing_identity_is_refused_never_replaced(string why, string kind, int id, int? rule)
    {
        using var server = new AlertServer(faults: false, new Row(73, 12, 4, 2));
        var device = why == "wrong parent device" ? 99 : 12;

        Assert.Null(await server.ResolveAsync(IntelligenceObjectTargets.Create(new(kind, id, device, rule), why)!));
    }

    [Fact]
    public async Task Rows_for_other_ids_are_never_taken_for_the_requested_one()
    {
        // A server that ignores the id and lists everything: only the exact row counts.
        using var server = new AlertServer(faults: true, new Row(80, 12, 4, 1, "port:1"), new Row(73, 12, 4, 0, "port:5")) { IgnoreId = true };

        var exact = await server.ResolveAsync(IntelligenceObjectTargets.Create(new("fault", 73, 12, 4), "Fault 73")!);
        var absent = await server.ResolveAsync(IntelligenceObjectTargets.Create(new("fault", 81, 12, 4), "Fault 81")!);

        Assert.Equal(73, exact!.Identity.Id);
        Assert.Null(absent);
    }

    [Fact]
    public async Task A_refused_read_is_reported_as_a_failure_not_as_an_absent_object()
    {
        using var server = new AlertServer(faults: true, new Row(73, 12, 4, 1, "port:5")) { RefuseAlerts = true };

        var failure = await Assert.ThrowsAsync<LibreNmsApiException>(() => server.ResolveAsync(IntelligenceObjectTargets.Create(new("fault", 73, 12, 4), "Fault")!));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    [Fact]
    public async Task Cancellation_reaches_the_lookup()
    {
        using var server = new AlertServer(faults: false, new Row(73, 12, 4, 1));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.ResolveAsync(IntelligenceObjectTargets.Create(new("alert", 73, 12, 4), "Alert")!, cancelled.Token));
    }

    /// <summary>A loopback LibreNMS answering only the alert routes, 26.9 (/alerts) or 26.10 (/faults).</summary>
    internal sealed class AlertServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _faults;
        private readonly Row[] _rows;
        private readonly LibreNmsTransport _transport = new(NullLogger<LibreNmsTransport>.Instance);
        private readonly LibreNmsClient _client;

        public AlertServer(bool faults, params Row[] rows)
        {
            _faults = faults;
            _rows = rows;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
            _client = new LibreNmsClient(_transport, NullLogger<LibreNmsClient>.Instance);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _client.Connect(new LibreNmsConnection(new Uri($"http://127.0.0.1:{port}/"), "example-token"));
        }

        public bool IgnoreId { get; init; }
        public bool RefuseAlerts { get; init; }
        public List<string> Requests { get; } = new();
        public LibreNmsClient Client => _client;
        public LibreNmsConnection Connection => _client.Connection!;

        public Task<SpotlightOption?> ResolveAsync(SpotlightOption target, CancellationToken cancellationToken = default)
            => IntelligenceObjectContextTests.Context(_client).ResolveObjectAsync(target, cancellationToken);

        private (int Status, string Body) Answer(string target)
        {
            lock (Requests) Requests.Add(target);
            var (path, query) = target.Split('?', 2) is [var p, var q] ? (p, q) : (target, "");
            var parts = path.Trim('/').Split('/').Skip(2).ToArray();
            var route = _faults ? "faults" : "alerts";
            if (parts.Length == 0 || (parts[0] != "faults" && parts[0] != "alerts")) return (404, "{\"status\":\"error\",\"message\":\"Route does not exist\"}");
            if (parts[0] != route) return (404, "{\"status\":\"error\",\"message\":\"Route does not exist\"}");
            // Core's source probe still succeeds; the alert read itself is refused.
            if (RefuseAlerts && query != "state=-1") return (403, "{\"status\":\"error\",\"message\":\"Insufficient permissions\"}");

            var states = query.Split('&').FirstOrDefault(q => q.StartsWith("state=", StringComparison.Ordinal)) is { } s
                ? Uri.UnescapeDataString(s["state=".Length..]).Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToHashSet()
                : new HashSet<int> { 1 };
            var rows = _rows.Where(r => states.Contains(r.State)
                && (IgnoreId || parts.Length < 2 || r.Id.ToString(CultureInfo.InvariantCulture) == parts[1]));
            var json = JsonSerializer.Serialize(rows.Select(r => new Dictionary<string, object?>
            {
                ["id"] = r.Id, ["device_id"] = r.DeviceId, ["rule_id"] = r.RuleId, ["state"] = r.State,
                ["severity"] = "critical", ["timestamp"] = "2026-11-16 10:00:00", ["entity_key"] = r.EntityKey,
            }));
            return (200, $"{{\"status\":\"ok\",\"alerts\":{json},\"count\":{rows.Count()}}}");
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient connection;
                try { connection = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = connection;
                    var stream = connection.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var (status, body) = Answer(requestLine.Split(' ')[1]);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(bytes);
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _client.Dispose();
            _transport.Dispose();
        }
    }
}
