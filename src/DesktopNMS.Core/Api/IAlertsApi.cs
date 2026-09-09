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
