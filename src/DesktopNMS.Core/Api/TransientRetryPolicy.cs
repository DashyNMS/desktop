using System.Net;
using System.Net.Http;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Decides whether a failed LibreNMS request is worth retrying automatically,
/// and how long to wait before doing so. Kept separate from
/// <see cref="LibreNmsTransport"/> so the policy itself - which failures
/// count as transient, how the backoff grows - is unit-testable without a
/// live or faked HTTP stack.
/// </summary>
public static class TransientRetryPolicy
{
    /// <summary>
    /// Beyond the transport's existing single immediate retry for a possibly-
    /// stale pooled connection, this many further attempts get an increasing,
    /// jittered wait before giving up - enough to smooth over a server
    /// struggling for a few seconds, without meaningfully delaying the next
    /// scheduled poll or leaving an interactive action (a manual Refresh
    /// click) hanging too long.
    /// </summary>
    public const int MaxAttempts = 3;

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Only GET and PUT are retried automatically - both are idempotent for
    /// every endpoint this app currently calls (polling reads, and
    /// acknowledge/unmute, which just re-applies the same state). POST is
    /// deliberately excluded: a request that creates something server-side
    /// (once this app has any) may have already succeeded even though the
    /// response was lost, and retrying it could create a duplicate.
    /// </summary>
    public static bool IsRetryable(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Put;

    /// <summary>
    /// A server-side or connectivity failure worth retrying - LibreNMS or its
    /// reverse proxy struggling momentarily (502/503/504), or the connection/
    /// timeout layer failing outright with no HTTP response at all - rather
    /// than a definite answer the server has no intention of changing (auth,
    /// not found, validation, and the like).
    /// </summary>
    public static bool IsTransientFailure(LibreNmsApiException ex) =>
        ex.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || ex.StatusCode is null;

    /// <summary>
    /// Exponential backoff (attempt 1 -&gt; ~375-500ms, attempt 2 -&gt; ~750ms-1s,
    /// attempt 3 -&gt; ~1.5-2s) with equal jitter - half the computed delay is
    /// fixed, half is random - so several independent pollers hitting the
    /// same struggling server do not all retry in lockstep.
    /// </summary>
    public static TimeSpan ComputeBackoffDelay(int attempt)
    {
        var exponential = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var half = exponential / 2;
        return half + (half * Random.Shared.NextDouble());
    }
}
