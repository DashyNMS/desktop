using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>Alert endpoints of the LibreNMS API.</summary>
public interface IAlertsApi
{
    /// <summary>GET /api/v0/alerts</summary>
    Task<IReadOnlyList<Alert>> ListAsync(AlertQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/alerts/{id}. Returns null when the alert no longer exists.</summary>
    Task<Alert?> GetAsync(int alertId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PUT /api/v0/alerts/{id}. Moves the alert to state 2 (acknowledged).
    /// </summary>
    /// <param name="note">Appended to the alert's note by the server, with your username and a timestamp.</param>
    /// <param name="untilClear">
    /// When true the acknowledgement holds until the alert clears. When false it
    /// is dropped as soon as the alert changes state, so it re-notifies.
    /// </param>
    Task AcknowledgeAsync(int alertId, string? note = null, bool untilClear = true, CancellationToken cancellationToken = default);

    /// <summary>PUT /api/v0/alerts/unmute/{id}. Returns the alert to state 1 (active).</summary>
    Task UnmuteAsync(int alertId, string? note = null, CancellationToken cancellationToken = default);
}

/// <summary>Alert rule endpoints.</summary>
public interface IAlertRulesApi
{
    /// <summary>GET /api/v0/rules</summary>
    Task<IReadOnlyList<AlertRule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/rules/{id}</summary>
    Task<AlertRule?> GetAsync(int ruleId, CancellationToken cancellationToken = default);
}

/// <summary>Device endpoints.</summary>
public interface IDevicesApi
{
    /// <summary>GET /api/v0/devices</summary>
    Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/devices/{idOrHostname}</summary>
    Task<Device?> GetAsync(string idOrHostname, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/maintenance. Whether the device currently sits
    /// inside an active maintenance window.
    /// </summary>
    /// <remarks>
    /// This is the only maintenance-related read the versioned LibreNMS API
    /// exposes: there is no endpoint to list, edit or cancel a maintenance
    /// window, only to create a new one and to ask this one boolean question
    /// per device. Checking every device therefore costs one request each;
    /// callers should batch with bounded concurrency and cache the result.
    /// </remarks>
    Task<bool> IsUnderMaintenanceAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/availability. Uptime percentage over four
    /// fixed windows (1 day, 7 days, 30 days, 1 year).
    /// </summary>
    Task<IReadOnlyList<AvailabilityWindow>> GetAvailabilityAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/devices/{id}/outages. Every downtime incident LibreNMS has recorded for the device.</summary>
    Task<IReadOnlyList<DeviceOutage>> GetOutagesAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/discover. Queues an on-demand rediscovery of
    /// the device - a GET despite the side effect, per LibreNMS's own API.
    /// There is no separate "poll now" endpoint; discovery is the closest the
    /// versioned API exposes. Returns the server's own confirmation message
    /// (e.g. "Device will be rediscovered") rather than waiting for the
    /// rediscovery itself to finish, which happens asynchronously on LibreNMS's side.
    /// </summary>
    Task<string> DiscoverAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Instance-level endpoints.</summary>
public interface ISystemApi
{
    /// <summary>GET /api/v0/system. Doubles as the credential check.</summary>
    Task<SystemInfo> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Sensor (health graph) endpoints.</summary>
public interface ISensorsApi
{
    /// <summary>
    /// GET /api/v0/resources/sensors. Unlike device maintenance status, this
    /// returns every sensor on every device in one call, so it is the one to
    /// use for a fleet-wide health view.
    /// </summary>
    Task<IReadOnlyList<Sensor>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Layer-2 neighbour discovery endpoints.</summary>
public interface ILinksApi
{
    /// <summary>
    /// GET /api/v0/devices/{id}/links. The neighbours LibreNMS has discovered
    /// (via LLDP/CDP/FDP/etc.) attached to this device's ports.
    /// </summary>
    Task<IReadOnlyList<NetworkLink>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Port (network interface) endpoints.</summary>
public interface IPortsApi
{
    /// <summary>
    /// GET /api/v0/devices/{id}/ports. Unlike sensors, LibreNMS has no
    /// fleet-wide ports endpoint - only per device - so this is fetched on
    /// demand by whatever needs one device's interfaces, not by a shared poller.
    /// </summary>
    Task<IReadOnlyList<Port>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/ip. Every IPv4/IPv6 address bound to any of
    /// the device's interfaces, keyed by <see cref="DeviceIpAddress.PortId"/>
    /// rather than returned per-port - a device can have several addresses
    /// on one interface (secondaries, HSRP/VRRP), or none at all on plenty
    /// (an access-only switchport, an unrouted management VLAN).
    /// </summary>
    Task<IReadOnlyList<DeviceIpAddress>> ListIpAddressesAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>MAC address forwarding table (FDB) endpoints.</summary>
public interface IFdbApi
{
    /// <summary>GET /api/v0/devices/{id}/fdb. Every MAC address the switch has learned, and which port/VLAN it was seen on.</summary>
    Task<IReadOnlyList<FdbEntry>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>ARP table endpoints.</summary>
public interface IArpApi
{
    /// <summary>
    /// GET /api/v0/resources/ip/arp/all?device={id}. Every IPv4-to-MAC
    /// mapping the device has resolved. Unlike most per-device endpoints,
    /// this one lives under /resources rather than /devices/{id} - LibreNMS's
    /// own API groups ARP by IP/network/MAC query first, device second.
    /// </summary>
    Task<IReadOnlyList<ArpEntry>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>VLAN endpoints.</summary>
public interface IVlansApi
{
    /// <summary>
    /// GET /api/v0/resources/vlans. Every VLAN LibreNMS knows about, across
    /// every device - there is no per-device variant that also returns the
    /// internal <see cref="Vlan.VlanId"/> a caller would need to resolve
    /// <see cref="FdbEntry.VlanId"/> (the per-device /devices/{id}/vlans
    /// endpoint omits it), so this fetches the whole list and callers filter
    /// by <see cref="Vlan.DeviceId"/> themselves.
    /// </summary>
    Task<IReadOnlyList<Vlan>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Device group endpoints.</summary>
public interface IDeviceGroupsApi
{
    /// <summary>GET /api/v0/devicegroups. Every saved device group (dynamic or static) defined on the server.</summary>
    Task<IReadOnlyList<DeviceGroup>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/groups. Every device group this one specific
    /// device belongs to - a device commonly belongs to more than one (e.g.
    /// both a site group and a role group). Cheap (one call), so this is
    /// what a single device's own view should use - see
    /// <see cref="GetMembershipByDeviceAsync"/> for the fleet-wide case.
    /// </summary>
    Task<IReadOnlyList<DeviceGroup>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every group's membership as one reverse index (device id -> the names
    /// of every group it belongs to), for filtering a whole device list at
    /// once. There is no bulk fleet-wide "groups per device" endpoint (only
    /// the single-device <see cref="ListForDeviceAsync"/>, too many calls to
    /// use for every device in the fleet, and the group-centric
    /// GET /api/v0/devicegroups/{id}, used here instead), so this calls that
    /// once per group (see <see cref="ListAsync"/>) and merges the results.
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetMembershipByDeviceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// CPU/memory/disk endpoints. LibreNMS keeps these in separate tables from
/// <see cref="Sensor"/> and has no fleet-wide listing for them either - like
/// <see cref="ILinksApi"/> and <see cref="IPortsApi"/>, each is fetched on
/// demand for one device at a time.
/// </summary>
public interface IDeviceHealthApi
{
    /// <summary>
    /// GET /api/v0/devices/{id}/health/processor(/{sensor_id}). LibreNMS's
    /// list call for a health type only returns each sensor's id and
    /// description - the actual reading needs a second call per id, which
    /// this issues concurrently (bounded) rather than one by one.
    /// </summary>
    Task<IReadOnlyList<ProcessorSensor>> ListProcessorsAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/devices/{id}/health/mempool(/{sensor_id}) - see <see cref="ListProcessorsAsync"/>.</summary>
    Task<IReadOnlyList<MempoolSensor>> ListMempoolsAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/devices/{id}/health/storage(/{sensor_id}) - see <see cref="ListProcessorsAsync"/>.</summary>
    Task<IReadOnlyList<StorageVolume>> ListStorageAsync(int deviceId, CancellationToken cancellationToken = default);
}
