using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>Log endpoints. Only the alert log is used so far.</summary>
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
}
