using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
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

    public async Task<string> DiscoverAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/discover";
        using var document = await _transport.SendAsync(HttpMethod.Get, url, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("message", out var message)
            && message.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        return "Device will be rediscovered.";
    }

    public async Task<AddDeviceResult> AddAsync(AddDeviceRequest request, CancellationToken cancellationToken = default)
    {
        using var document = await _transport.SendAsync(HttpMethod.Post, "devices", body: request, cancellationToken: cancellationToken).ConfigureAwait(false);

        var message = document.RootElement.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            && messageElement.GetString() is { Length: > 0 } text
                ? text
                : "Device added.";

        int? deviceId = null;
        if (document.RootElement.TryGetProperty("devices", out var devices)
            && devices.ValueKind == JsonValueKind.Array
            && devices.GetArrayLength() > 0
            && devices[0].TryGetProperty("device_id", out var idElement))
        {
            deviceId = JsonSerializer.Deserialize<int?>(idElement.GetRawText(), LibreNmsJson.Options);
        }

        return new AddDeviceResult(message, deviceId);
    }

    public async Task UpdateFieldsAsync(int deviceId, IReadOnlyDictionary<string, string?> fields, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture);
        var request = new UpdateDeviceFieldsRequest
        {
            Field = fields.Keys.ToArray(),
            Data = fields.Values.ToArray(),
        };

        // Response is a bare JSON array (unlike almost every other endpoint's
        // object envelope), e.g. [{"status":"ok","message":"..."}] - nothing
        // here needs its content, since SendAsync already throws on a
        // non-success HTTP status.
        using var _ = await _transport.SendAsync(HttpMethod.Patch, url, body: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(int deviceId, string newHostname, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/rename/" + Uri.EscapeDataString(newHostname);

        try
        {
            using var _ = await _transport.SendAsync(HttpMethod.Patch, url, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (LibreNmsApiException renameException)
        {
            // Confirmed against a live instance: this endpoint sometimes
            // renames the device successfully while still reporting failure
            // to this client - a malformed (non-JSON) body on an otherwise
            // successful request, or even an HTTP 500 after the rename had
            // already committed server-side. Guessing from the status code
            // alone was not reliable enough (both shapes have been observed
            // for a rename that did go through), so this re-reads the device
            // directly and only re-throws if the hostname genuinely did not
            // change to what was requested.
            //
            // The verification read can itself fail for an unrelated reason
            // (also confirmed live: a device whose Location LibreNMS's own
            // geocoding feature had resolved to an object instead of a plain
            // string, tripping a completely different deserialisation error)
            // - caught explicitly by name (renameException, not a bare
            // throw;) so a failure here re-raises the original rename
            // failure rather than a confusing second error about something
            // the caller never asked about.
            Device? confirmed;
            try
            {
                confirmed = await GetAsync(deviceId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            }
            catch (LibreNmsApiException)
            {
                ExceptionDispatchInfo.Capture(renameException).Throw();
                return;
            }

            if (confirmed is null || !string.Equals(confirmed.Hostname, newHostname, StringComparison.OrdinalIgnoreCase))
            {
                ExceptionDispatchInfo.Capture(renameException).Throw();
            }
        }
    }

    public async Task<string> DeleteAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture);
        using var document = await _transport.SendAsync(HttpMethod.Delete, url, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            && messageElement.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        return "Device deleted.";
    }
}

internal sealed class UpdateDeviceFieldsRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("field")]
    public string[] Field { get; set; } = Array.Empty<string>();

    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public string?[] Data { get; set; } = Array.Empty<string?>();
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

    public Task<IReadOnlyList<DeviceGroup>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default)
    {
        var url = "devices/" + deviceId.ToString(CultureInfo.InvariantCulture) + "/groups";
        return _transport.GetCollectionAsync<DeviceGroup>(url, "groups", cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetMembershipByDeviceAsync(CancellationToken cancellationToken = default)
    {
        var groups = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<string>>();
        }

        var results = await FetchAllMembershipsAsync(groups, cancellationToken).ConfigureAwait(false);

        var membership = new Dictionary<int, List<string>>();
        foreach (var (group, deviceIds) in results)
        {
            foreach (var deviceId in deviceIds)
            {
                if (!membership.TryGetValue(deviceId, out var names))
                {
                    names = new List<string>();
                    membership[deviceId] = names;
                }

                names.Add(group.Name);
            }
        }

        return membership.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetMemberCountsAsync(IReadOnlyList<DeviceGroup> groups, CancellationToken cancellationToken = default)
    {
        if (groups.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var results = await FetchAllMembershipsAsync(groups, cancellationToken).ConfigureAwait(false);
        return results.ToDictionary(r => r.Group.Id, r => r.DeviceIds.Count);
    }

    public async Task<IReadOnlyList<int>> GetMemberDeviceIdsAsync(int groupId, CancellationToken cancellationToken = default)
    {
        var url = "devicegroups/" + groupId.ToString(CultureInfo.InvariantCulture);
        var members = await _transport.GetCollectionAsync<DeviceGroupMember>(url, "devices", cancellationToken).ConfigureAwait(false);
        return members.Select(m => m.DeviceId).ToArray();
    }

    /// <summary>Shared by <see cref="GetMembershipByDeviceAsync"/> and <see cref="GetMemberCountsAsync"/> - one bounded-concurrency pass over every group's own membership call.</summary>
    private async Task<IReadOnlyList<(DeviceGroup Group, IReadOnlyList<int> DeviceIds)>> FetchAllMembershipsAsync(IReadOnlyList<DeviceGroup> groups, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);

        var tasks = groups.Select(async group =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var deviceIds = await GetMemberDeviceIdsAsync(group.Id, cancellationToken).ConfigureAwait(false);
                return (Group: group, DeviceIds: deviceIds);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task CreateAsync(string name, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default)
    {
        var request = new DeviceGroupWriteRequest
        {
            Name = name,
            Description = description,
            Devices = deviceIds.ToArray(),
        };

        using var _ = await _transport.SendAsync(HttpMethod.Post, "devicegroups", body: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(string currentName, string newName, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default)
    {
        var url = "devicegroups/" + Uri.EscapeDataString(currentName);
        var request = new DeviceGroupWriteRequest
        {
            Name = newName,
            Description = description,
            Devices = deviceIds.ToArray(),
        };

        using var _ = await _transport.SendAsync(HttpMethod.Patch, url, body: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var url = "devicegroups/" + Uri.EscapeDataString(name);
        using var _ = await _transport.SendAsync(HttpMethod.Delete, url, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The membership call's own shape - just enough to know which device this row is.</summary>
    private sealed class DeviceGroupMember
    {
        [System.Text.Json.Serialization.JsonPropertyName("device_id")]
        public int DeviceId { get; set; }
    }
}

/// <summary>Body for creating/updating a static device group - always sends "type": "static" since this app has no UI for a dynamic group's rules.</summary>
internal sealed class DeviceGroupWriteRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("desc")]
    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "static";

    [System.Text.Json.Serialization.JsonPropertyName("devices")]
    public int[] Devices { get; set; } = Array.Empty<int>();
}

/// <summary>Implementation of <see cref="ILocationsApi"/>.</summary>
internal sealed class LocationsApi : ILocationsApi
{
    private readonly ILibreNmsTransport _transport;

    public LocationsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<Location>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<Location>("resources/locations", "locations", cancellationToken);

    public async Task CreateAsync(string name, double lat, double lng, bool fixedCoordinates, CancellationToken cancellationToken = default)
    {
        var request = new LocationWriteRequest
        {
            Name = name,
            Latitude = lat,
            Longitude = lng,
            FixedCoordinates = fixedCoordinates ? 1 : 0,
        };

        using var _ = await _transport.SendAsync(HttpMethod.Post, "locations/", body: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(int id, double lat, double lng, bool fixedCoordinates, CancellationToken cancellationToken = default)
    {
        var url = "locations/" + id.ToString(CultureInfo.InvariantCulture);
        var request = new LocationWriteRequest
        {
            Latitude = lat,
            Longitude = lng,
            FixedCoordinates = fixedCoordinates ? 1 : 0,
        };

        using var _ = await _transport.SendAsync(HttpMethod.Patch, url, body: request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var url = "locations/" + id.ToString(CultureInfo.InvariantCulture);
        using var _ = await _transport.SendAsync(HttpMethod.Delete, url, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Body for creating/updating a location. Name is omitted (left null) on an update - LibreNMS's own edit_location endpoint does not document a rename parameter.</summary>
internal sealed class LocationWriteRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("location")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lat")]
    public double Latitude { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lng")]
    public double Longitude { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("fixed_coordinates")]
    public int FixedCoordinates { get; set; }
}

/// <summary>Implementation of <see cref="IPollerGroupsApi"/>.</summary>
internal sealed class PollerGroupsApi : IPollerGroupsApi
{
    private readonly ILibreNmsTransport _transport;

    public PollerGroupsApi(ILibreNmsTransport transport) => _transport = transport;

    public Task<IReadOnlyList<PollerGroup>> ListAsync(CancellationToken cancellationToken = default)
        => _transport.GetCollectionAsync<PollerGroup>("poller_group", "get_poller_group", cancellationToken);
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
