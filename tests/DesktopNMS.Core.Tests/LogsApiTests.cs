using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class LogsApiTests
{
    [Fact]
    public async Task Event_log_for_one_device_puts_the_device_in_the_path()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListEventLogAsync(42, 25);

        Assert.Equal("logs/eventlog/42?limit=25&sortorder=DESC", transport.Url);
    }

    [Fact]
    public async Task Event_log_for_every_device_leaves_the_device_out()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListEventLogAsync(null, 25);

        Assert.Equal("logs/eventlog?limit=25&sortorder=DESC", transport.Url);
    }

    [Fact]
    public async Task Alert_log_for_every_device_leaves_the_device_out()
    {
        var transport = new RecordingTransport();

        await new LogsApi(transport).ListAlertLogAsync(null, 10);

        Assert.Equal("logs/alertlog?limit=10&sortorder=DESC", transport.Url);
    }

    [Fact]
    public void A_fleet_wide_event_log_entry_names_its_device()
    {
        // The shape LibreNMS's list_logs returns with no device: the eventlog
        // row plus the joined hostname and sysName.
        const string json = """
            {"hostname":"sw-core-01.example.net","sysName":"sw-core-01","host":7,"event_id":901,"device_id":7,
             "datetime":"2026-10-01 08:12:41","message":"Interface went down","type":"interface","username":"","severity":4}
            """;

        var entry = JsonSerializer.Deserialize<EventLogEntry>(json, LibreNmsJson.Options)!;

        Assert.Equal(7, entry.DeviceId);
        Assert.Equal("sw-core-01.example.net", entry.Hostname);
        Assert.Equal("sw-core-01", entry.SysName);
    }

    private sealed class RecordingTransport : ILibreNmsTransport
    {
        public string? Url { get; private set; }

        public LibreNmsConnection? Connection => null;

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonDocument.Parse("{\"status\":\"ok\"}"));

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            Url = relativeUrl;
            return Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());
        }

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
