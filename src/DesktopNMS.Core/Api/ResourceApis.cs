using System.Globalization;
using System.Linq;
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

    public Task<IReadOnlyList<AvailabilityWindow>> GetAvailabilityAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/availability";
        return _transport.GetCollectionAsync<AvailabilityWindow>(url, "availability", cancellationToken);
    }

    public Task<IReadOnlyList<DeviceOutage>> GetOutagesAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/outages";
        return _transport.GetCollectionAsync<DeviceOutage>(url, "outages", cancellationToken);
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

/// <summary>Implementation of <see cref="ILinksApi"/>.</summary>
internal sealed class LinksApi : ILinksApi
{
    private readonly ILibreNmsTransport _transport;

    public LinksApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<NetworkLink>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/links";
        return _transport.GetCollectionAsync<NetworkLink>(url, "links", cancellationToken);
    }
}

/// <summary>Implementation of <see cref="IPortsApi"/>.</summary>
internal sealed class PortsApi : IPortsApi
{
    /// <summary>
    /// Without an explicit "columns" query parameter, LibreNMS's ports-for-device
    /// endpoint returns only ifName per port - confirmed by inspecting the raw
    /// response, not documented behaviour worth relying on from memory alone.
    /// Everything the Ports tab shows has to be asked for by name.
    /// </summary>
    private const string Columns =
        "port_id,device_id,ifIndex,ifName,ifDescr,ifAlias,ifType,ifSpeed,ifDuplex,ifMtu," +
        "ifPhysAddress,ifOperStatus,ifAdminStatus,ifInOctets_rate,ifOutOctets_rate," +
        "ifInErrors_delta,ifOutErrors_delta,ifVlan,ignore,disabled,deleted";

    private readonly ILibreNmsTransport _transport;

    public PortsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Port>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/ports?columns=" + Columns;
        return _transport.GetCollectionAsync<Port>(url, "ports", cancellationToken);
    }

    public Task<IReadOnlyList<DeviceIpAddress>> ListIpAddressesAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/ip";
        return _transport.GetCollectionAsync<DeviceIpAddress>(url, "addresses", cancellationToken);
    }
}

/// <summary>Implementation of <see cref="IFdbApi"/>.</summary>
internal sealed class FdbApi : IFdbApi
{
    private readonly ILibreNmsTransport _transport;

    public FdbApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<FdbEntry>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/fdb";
        return _transport.GetCollectionAsync<FdbEntry>(url, "ports_fdb", cancellationToken);
    }
}

/// <summary>Implementation of <see cref="IArpApi"/>.</summary>
internal sealed class ArpApi : IArpApi
{
    private readonly ILibreNmsTransport _transport;

    public ArpApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<ArpEntry>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "resources/ip/arp/all?device=" + deviceId.ToString(CultureInfo.InvariantCulture);
        return _transport.GetCollectionAsync<ArpEntry>(url, "arp", cancellationToken);
    }
}

/// <summary>Implementation of <see cref="IDeviceGroupsApi"/>.</summary>
internal sealed class DeviceGroupsApi : IDeviceGroupsApi
{
    /// <summary>
    /// Caps how many /devicegroups/{id} calls run at once, so a server with
    /// many groups does not get them all fired in one burst - same reasoning
    /// as <see cref="DeviceHealthApi"/>'s own concurrency cap.
    /// </summary>
    private const int MaxConcurrency = 6;

    private readonly ILibreNmsTransport _transport;

    public DeviceGroupsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<DeviceGroup>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<DeviceGroup>("devicegroups", "groups", cancellationToken);

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetMembershipByDeviceAsync(CancellationToken cancellationToken = default)
    {
        var groups = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<string>>();
        }

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var memberTasks = groups.Select(async group =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = "devicegroups/" + group.Id.ToString(CultureInfo.InvariantCulture);
                var members = await _transport.GetCollectionAsync<DeviceGroupMember>(url, "devices", cancellationToken).ConfigureAwait(false);
                return (group.Name, Members: members);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(memberTasks).ConfigureAwait(false);

        var membership = new Dictionary<int, List<string>>();
        foreach (var (name, members) in results)
        {
            foreach (var member in members)
            {
                if (!membership.TryGetValue(member.DeviceId, out var names))
                {
                    names = new List<string>();
                    membership[member.DeviceId] = names;
                }

                names.Add(name);
            }
        }

        return membership.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>The membership call's own shape - just enough to know which device this row is.</summary>
    private sealed class DeviceGroupMember
    {
        [System.Text.Json.Serialization.JsonPropertyName("device_id")]
        public int DeviceId { get; set; }
    }
}

/// <summary>Implementation of <see cref="IVlansApi"/>.</summary>
internal sealed class VlansApi : IVlansApi
{
    private readonly ILibreNmsTransport _transport;

    public VlansApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Vlan>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<Vlan>("resources/vlans", "vlans", cancellationToken);
}

/// <summary>Implementation of <see cref="IDeviceHealthApi"/>.</summary>
internal sealed class DeviceHealthApi : IDeviceHealthApi
{
    /// <summary>
    /// Caps how many per-sensor detail requests run at once, so a device with
    /// many CPUs or disks (a 16-core box, a device with a dozen mount points)
    /// does not fire that many requests in one burst.
    /// </summary>
    private const int MaxConcurrency = 6;

    private readonly ILibreNmsTransport _transport;

    public DeviceHealthApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<ProcessorSensor>> ListProcessorsAsync(int deviceId, CancellationToken cancellationToken = default)
        => ListAsync<ProcessorSensor>(deviceId, "processor", cancellationToken);

    public Task<IReadOnlyList<MempoolSensor>> ListMempoolsAsync(int deviceId, CancellationToken cancellationToken = default)
        => ListAsync<MempoolSensor>(deviceId, "mempool", cancellationToken);

    public Task<IReadOnlyList<StorageVolume>> ListStorageAsync(int deviceId, CancellationToken cancellationToken = default)
        => ListAsync<StorageVolume>(deviceId, "storage", cancellationToken);

    private async Task<IReadOnlyList<T>> ListAsync<T>(int deviceId, string healthType, CancellationToken cancellationToken)
        where T : class
    {
        var baseUrl = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/health/" + healthType;
        var refs = await _transport.GetCollectionAsync<HealthGraphRef>(baseUrl, "graphs", cancellationToken).ConfigureAwait(false);

        if (refs.Count == 0)
        {
            return Array.Empty<T>();
        }

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var detailTasks = refs.Select(async reference =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var url = baseUrl + "/" + reference.SensorId.ToString(CultureInfo.InvariantCulture);
                var detail = await _transport.GetCollectionAsync<T>(url, "graphs", cancellationToken).ConfigureAwait(false);
                return detail.Count > 0 ? detail[0] : null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(detailTasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <summary>The list call's own shape - just enough to know which ids to fetch detail for.</summary>
    private sealed class HealthGraphRef
    {
        [System.Text.Json.Serialization.JsonPropertyName("sensor_id")]
        public int SensorId { get; set; }
    }
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

    public Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(
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
            $"logs/eventlog/{deviceId}?limit={limit}&sortorder=DESC");

        return _transport.GetCollectionAsync<EventLogEntry>(url, "logs", cancellationToken);
    }
}
