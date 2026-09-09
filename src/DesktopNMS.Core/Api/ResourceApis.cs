using System.Globalization;
using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>Implementation of <see cref="IAlertsApi"/> over <see cref="ILibreNmsTransport"/>.</summary>
internal sealed class AlertsApi : IAlertsApi
{
    private readonly ILibreNmsTransport _transport;

    public AlertsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Alert>> ListAsync(AlertQuery? query = null, CancellationToken cancellationToken = default)
    {
        var url = (query ?? AlertQuery.Open).ToRelativeUrl();
        return _transport.GetCollectionAsync<Alert>(url, "alerts", cancellationToken);
    }

    public async Task<Alert?> GetAsync(int alertId, CancellationToken cancellationToken = default)
    {
        var url = "alerts/" + alertId.ToString(CultureInfo.InvariantCulture);
        var alerts = await _transport.GetCollectionAsync<Alert>(url, "alerts", cancellationToken).ConfigureAwait(false);
        return alerts.Count > 0 ? alerts[0] : null;
    }

    public async Task AcknowledgeAsync(int alertId, string? note = null, bool untilClear = true, CancellationToken cancellationToken = default)
    {
        // The server dereferences both fields unconditionally, so always send them.
        var body = new AcknowledgeRequest
        {
            Note = note ?? string.Empty,
            UntilClear = untilClear,
        };

        var response = await _transport
            .SendAsync(HttpMethod.Put, "alerts/" + alertId.ToString(CultureInfo.InvariantCulture), body, cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }

    public async Task UnmuteAsync(int alertId, string? note = null, CancellationToken cancellationToken = default)
    {
        var body = new UnmuteRequest { Note = note ?? string.Empty };

        var response = await _transport
            .SendAsync(HttpMethod.Put, "alerts/unmute/" + alertId.ToString(CultureInfo.InvariantCulture), body, cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }
}

/// <summary>Implementation of <see cref="IAlertRulesApi"/>.</summary>
internal sealed class AlertRulesApi : IAlertRulesApi
{
    private readonly ILibreNmsTransport _transport;

    public AlertRulesApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<AlertRule>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<AlertRule>("rules", "rules", cancellationToken);

    public async Task<AlertRule?> GetAsync(int ruleId, CancellationToken cancellationToken = default)
    {
        var url = "rules/" + ruleId.ToString(CultureInfo.InvariantCulture);
        var rules = await _transport.GetCollectionAsync<AlertRule>(url, "rules", cancellationToken).ConfigureAwait(false);
        return rules.Count > 0 ? rules[0] : null;
    }
}

/// <summary>Implementation of <see cref="IDevicesApi"/>.</summary>
internal sealed class DevicesApi : IDevicesApi
{
    private readonly ILibreNmsTransport _transport;

    public DevicesApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<Device>("devices", "devices", cancellationToken);

    public async Task<Device?> GetAsync(string idOrHostname, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + Uri.EscapeDataString(idOrHostname);
        var devices = await _transport.GetCollectionAsync<Device>(url, "devices", cancellationToken).ConfigureAwait(false);
        return devices.Count > 0 ? devices[0] : null;
    }

    public async Task<bool> IsUnderMaintenanceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/maintenance";
        using var document = await _transport.SendAsync(HttpMethod.Get, url, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("is_under_maintenance", out var element))
        {
            return false;
        }

        // Reuse the same tolerant bool parsing as every other endpoint in this
        // client, in case this ever arrives as "1"/"0" like other columns do.
        return JsonSerializer.Deserialize<bool>(element.GetRawText(), LibreNmsJson.Options);
    }
}

/// <summary>Implementation of <see cref="ISensorsApi"/>.</summary>
internal sealed class SensorsApi : ISensorsApi
{
    private readonly ILibreNmsTransport _transport;

    public SensorsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Sensor>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<Sensor>("resources/sensors", "sensors", cancellationToken);
}

/// <summary>Implementation of <see cref="ISystemApi"/>.</summary>
internal sealed class SystemApi : ISystemApi
{
    private readonly ILibreNmsTransport _transport;

    public SystemApi(ILibreNmsTransport transport) => _transport = transport;

    public async Task<SystemInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _transport.GetCollectionAsync<SystemInfo>("system", "system", cancellationToken)
            .ConfigureAwait(false);

        return entries.Count > 0 ? entries[0] : new SystemInfo();
    }
}

internal sealed class AcknowledgeRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("until_clear")]
    public bool UntilClear { get; set; }
}

internal sealed class UnmuteRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;
}

/// <summary>Implementation of <see cref="ILogsApi"/>.</summary>
internal sealed class LogsApi : ILogsApi
{
    private readonly ILibreNmsTransport _transport;

    public LogsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(
        int deviceId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            limit = 1;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"logs/alertlog/{deviceId}?limit={limit}&sortorder=DESC");

        return _transport.GetCollectionAsync<AlertLogEntry>(url, "logs", cancellationToken);
    }

    public async Task<AlertLogEntry?> GetLatestForRuleAsync(
        int deviceId,
        int ruleId,
        int searchDepth = 50,
        CancellationToken cancellationToken = default)
    {
        // The endpoint filters by device but not by rule, so pull a window of
        // recent entries and pick the newest one for this rule.
        var entries = await ListAlertLogAsync(deviceId, searchDepth, cancellationToken).ConfigureAwait(false);

        AlertLogEntry? best = null;

        foreach (var entry in entries)
        {
            if (entry.RuleId != ruleId)
            {
                continue;
            }

            // Highest alert_log id wins; the server's sort order is not worth trusting blindly.
            if (best is null || entry.Id > best.Id)
            {
                best = entry;
            }
        }

        return best;
    }
}
