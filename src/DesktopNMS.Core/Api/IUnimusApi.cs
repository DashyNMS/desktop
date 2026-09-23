using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Client for Unimus's own API v2 (issue #115) - a second, independent
/// integration this app talks to directly, not through LibreNMS (LibreNMS
/// ships its own server-side Unimus client, but that only drives its web
/// UI's Config tab and isn't reachable via LibreNMS's versioned API).
/// </summary>
/// <remarks>
/// Confirmed against Unimus's official API v2 docs and cross-checked against
/// LibreNMS's own working client (<c>app/ApiClients/Unimus.php</c>), which
/// talks to the exact same endpoints this interface covers.
/// </remarks>
public interface IUnimusApi
{
    /// <summary>True once <see cref="Configure"/> has been called and not since undone by <see cref="Clear"/> - i.e. the integration is set up, not that it has been verified reachable.</summary>
    bool IsConfigured { get; }

    /// <summary>Points this client at a Unimus instance, replacing any previous configuration.</summary>
    void Configure(UnimusConnection connection);

    /// <summary>Disables the integration; subsequent calls fail as "not configured".</summary>
    void Clear();

    /// <summary>
    /// GET devices/findByAddress/{address}. Null on a 404 (no device at that
    /// address) - not an error, just a miss for this one candidate address.
    /// </summary>
    /// <remarks>
    /// Confirmed live: this only matches a device's IP
    /// (<see cref="UnimusDevice.Address"/>), never its hostname/description,
    /// and is scoped to Unimus's "Default Zone" unless a <c>zoneId</c> is
    /// known and passed - neither of which this app can assume. Device
    /// matching uses <see cref="ListAllDevicesAsync"/> + <see cref="Alerting.UnimusDeviceMatcher"/>
    /// instead; this method is kept for completeness and for a caller that
    /// already knows a specific IP and zone.
    /// </remarks>
    Task<UnimusDevice?> FindDeviceAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every device Unimus knows about, across every zone - pages through
    /// GET devices?page&amp;size internally. See <see cref="Alerting.UnimusDeviceMatcher"/>
    /// for why this, not <see cref="FindDeviceAsync"/>, is how a LibreNMS
    /// device gets matched to its Unimus entry.
    /// </summary>
    Task<IReadOnlyList<UnimusDevice>> ListAllDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>GET devices/{id}/backups/latest, with content included.</summary>
    Task<UnimusBackup?> GetLatestBackupAsync(int unimusDeviceId, CancellationToken cancellationToken = default);

    /// <summary>GET devices/{id}/backups?page&amp;size. Rows do not include content - see <see cref="GetBackupContentAsync"/>.</summary>
    Task<UnimusBackupPage> GetBackupsAsync(int unimusDeviceId, int page = 0, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one backup's content. Unimus has no fetch-a-single-backup
    /// endpoint, so - mirroring LibreNMS's own client - this re-fetches the
    /// page the backup was listed on and extracts it. Null if the backup
    /// isn't on that page (it moved, or the caller has a stale page number)
    /// or isn't a TEXT backup.
    /// </summary>
    Task<string?> GetBackupContentAsync(int unimusDeviceId, int backupId, int page = 0, int size = 50, CancellationToken cancellationToken = default);

    /// <summary>GET backups/diff?origId&amp;revId.</summary>
    Task<UnimusBackupDiff?> GetDiffAsync(int origBackupId, int revBackupId, CancellationToken cancellationToken = default);

    /// <summary>PATCH jobs/backup?id={id} - queues an on-demand backup for one device.</summary>
    Task<UnimusBackupJobResult> TriggerBackupAsync(int unimusDeviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the configured URL/token actually work, for Settings' "Test
    /// connection" action. There is no dedicated health-check endpoint in
    /// Unimus's API, so this piggybacks on <see cref="FindDeviceAsync"/> with
    /// a address that will almost certainly not exist - a 404 means the
    /// token and URL are good (Unimus authenticated the request and searched),
    /// a 401/403 means the token is wrong, and a connection failure means the
    /// URL or SSL setting is wrong.
    /// </summary>
    Task TestConnectionAsync(CancellationToken cancellationToken = default);
}
