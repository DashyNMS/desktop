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

    /// <summary>POST /api/v0/rules. <paramref name="request"/>.RuleId is ignored - always creates a new rule.</summary>
    Task CreateAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>PUT /api/v0/rules. <paramref name="request"/>.RuleId identifies which rule to update - LibreNMS's own edit_rule route takes the id in the body, not the URL.</summary>
    Task UpdateAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/v0/rules/{id}.</summary>
    Task DeleteAsync(int ruleId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Alert template endpoints. Unlike <see cref="IAlertRulesApi"/>, LibreNMS's
/// API has no delete route for templates (confirmed live via the server's
/// own /api/v0 route map) - list/create/edit is the whole surface.
/// </summary>
public interface IAlertTemplatesApi
{
    /// <summary>GET /api/v0/alert_templates</summary>
    Task<IReadOnlyList<AlertTemplate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /api/v0/alert_templates/{id}</summary>
    Task<AlertTemplate?> GetAsync(int templateId, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v0/alert_templates. <paramref name="request"/>.TemplateId is ignored - always creates a new template.</summary>
    Task CreateAsync(AlertTemplateWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>POST /api/v0/alert_templates with <paramref name="request"/>.TemplateId set - LibreNMS's own edit_alert_template route reuses the create route, addressed by that field rather than a distinct PUT/PATCH verb.</summary>
    Task UpdateAsync(AlertTemplateWriteRequest request, CancellationToken cancellationToken = default);
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
    /// POST /api/v0/devices/{id}/maintenance. Schedules a maintenance window
    /// for this device - see <see cref="DeviceMaintenanceRequest"/>. Returns
    /// the server's own confirmation message. There is no corresponding
    /// cancel/delete route (see this interface's remarks on
    /// <see cref="IsUnderMaintenanceAsync"/>): once scheduled, a window can
    /// only be ended early from LibreNMS's own web UI.
    /// </summary>
    Task<string> ScheduleMaintenanceAsync(int deviceId, DeviceMaintenanceRequest request, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// POST /api/v0/devices. Adds a new device - see <see cref="AddDeviceRequest"/>
    /// for what can be set. Only <see cref="AddDeviceRequest.Hostname"/> is
    /// required; LibreNMS tries each configured system SNMP credential in
    /// turn when none are given on the request itself.
    /// </summary>
    Task<AddDeviceResult> AddAsync(AddDeviceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v0/devices/{id}. Updates one or more columns on the device
    /// (LibreNMS's own "update_device_field" endpoint) - e.g. location,
    /// purpose, notes. <paramref name="fields"/> maps LibreNMS's own column
    /// name to the new value; a null value clears that field.
    /// </summary>
    Task UpdateFieldsAsync(int deviceId, IReadOnlyDictionary<string, string?> fields, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v0/devices/{id}/rename/{newHostname}. Changes the polled
    /// hostname/address itself - a distinct operation from
    /// <see cref="UpdateFieldsAsync"/>, since this is the identifier LibreNMS
    /// actually polls, not just a stored column. The device id is unchanged
    /// by a rename.
    /// </summary>
    Task RenameAsync(int deviceId, string newHostname, CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETE /api/v0/devices/{id}. Permanently removes the device and its
    /// history from LibreNMS - there is no undo. Returns the server's own
    /// confirmation message.
    /// </summary>
    Task<string> DeleteAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Distributed-poller group endpoints - see <see cref="AddDeviceRequest.PollerGroup"/>.</summary>
public interface IPollerGroupsApi
{
    /// <summary>GET /api/v0/poller_group. Every poller group configured on the instance - empty on a single-poller setup with none defined.</summary>
    Task<IReadOnlyList<PollerGroup>> ListAsync(CancellationToken cancellationToken = default);
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

    /// <summary>
    /// GET /api/v0/resources/links. Every discovered link on every device in
    /// one call - what the network map (issue #56) is built from. Each
    /// connection between two monitored devices usually appears twice, once
    /// from each end (see <see cref="Topology.NetworkTopology"/>).
    /// </summary>
    Task<IReadOnlyList<NetworkLink>> ListAllAsync(CancellationToken cancellationToken = default);
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

    /// <summary>GET /api/v0/devicegroups/{id}. The device ids belonging to one specific group - used to pre-check a static group's members when opening it for editing.</summary>
    Task<IReadOnlyList<int>> GetMemberDeviceIdsAsync(int groupId, CancellationToken cancellationToken = default);

    /// <summary>Member count per group id, one <see cref="GetMemberDeviceIdsAsync"/> call per group with bounded concurrency - for the Groups tab's Devices column.</summary>
    Task<IReadOnlyDictionary<int, int>> GetMemberCountsAsync(IReadOnlyList<DeviceGroup> groups, CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v0/devicegroups. Creates a new static (explicit device-list)
    /// group - this app has no UI for a dynamic group's rules, so
    /// <paramref name="deviceIds"/> is always required and "type" is always
    /// sent as "static".
    /// </summary>
    Task CreateAsync(string name, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v0/devicegroups/{currentName}. Addressed by the group's
    /// current name - <paramref name="newName"/> renames it via the request
    /// body, same convention as <see cref="IDevicesApi.RenameAsync"/> using a
    /// separate identifier for "where" versus "what it becomes". Like
    /// <see cref="CreateAsync"/>, always a static-group update.
    /// </summary>
    Task UpdateAsync(string currentName, string newName, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/v0/devicegroups/{name}. Works for a group of either type - deleting does not require understanding its rules.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// LibreNMS's own Locations resource - a named, geocoded site (see
/// <see cref="Location"/>). Distinct from <see cref="Device.Location"/>,
/// which is just a free-text column on the device itself: LibreNMS has no
/// bulk "assign these devices to this location" endpoint, so a device only
/// ends up "at" a location by having that exact name typed into its own
/// Location field (see <see cref="IDevicesApi.UpdateFieldsAsync"/>).
/// </summary>
public interface ILocationsApi
{
    /// <summary>GET /api/v0/resources/locations. Every location LibreNMS knows about.</summary>
    Task<IReadOnlyList<Location>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>POST /api/v0/locations/. Requires coordinates - LibreNMS has no "location with no coordinates" concept.</summary>
    Task CreateAsync(string name, double lat, double lng, bool fixedCoordinates, CancellationToken cancellationToken = default);

    /// <summary>
    /// PATCH /api/v0/locations/{id}. Addressed by id, not name (unlike
    /// <see cref="IDeviceGroupsApi.UpdateAsync"/>) - LibreNMS's own docs only
    /// list lat/lng as editable here, no rename parameter, so there is
    /// nothing to rename anyway.
    /// </summary>
    Task UpdateAsync(int id, double lat, double lng, bool fixedCoordinates, CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/v0/locations/{id}.</summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
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
