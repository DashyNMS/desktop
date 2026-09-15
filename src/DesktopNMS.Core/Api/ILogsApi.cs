using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>Log endpoints.</summary>
public interface ILogsApi
{
    /// <summary>
    /// GET /api/v0/logs/alertlog/{device}, newest first.
    /// </summary>
    /// <param name="deviceId">Device to fetch entries for. The route also accepts a hostname.</param>
    /// <param name="limit">Maximum entries to return. The server defaults to 50.</param>
    Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(
        int deviceId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent alert log entry for a rule on a device, which is
    /// the one carrying the faults for the alert currently showing.
    /// </summary>
    Task<AlertLogEntry?> GetLatestForRuleAsync(
        int deviceId,
        int ruleId,
        int searchDepth = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/logs/eventlog/{device}, newest first - LibreNMS's general
    /// audit trail for the device, distinct from the alert log.
    /// </summary>
    /// <remarks>
    /// There is no separate offset/skip parameter here (a "start" query
    /// parameter was tried and confirmed, against a real server, to have no
    /// effect - the same top <paramref name="limit"/> entries came back
    /// regardless). "Load more" further back therefore means re-issuing this
    /// call with a larger <paramref name="limit"/> and taking only the tail
    /// beyond what is already on screen, rather than true paging.
    /// </remarks>
    /// <param name="deviceId">Device to fetch entries for. The route also accepts a hostname.</param>
    /// <param name="limit">Maximum entries to return, counted from newest. The server defaults to 50.</param>
    Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(
        int deviceId,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
