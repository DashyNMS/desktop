using System.Text.Json;

namespace DesktopNMS.Core.Api;

/// <summary>
/// The thin HTTP layer the resource APIs sit on. Kept separate so new resource
/// APIs (ports, services, health, ...) can be added without touching transport,
/// and so they can be unit tested against a fake.
/// </summary>
public interface ILibreNmsTransport
{
    /// <summary>The connection currently in use, or null if not signed in.</summary>
    LibreNmsConnection? Connection { get; }

    /// <summary>
    /// Issues a request and returns the parsed envelope. Throws
    /// <see cref="LibreNmsApiException"/> for transport, HTTP and API errors.
    /// The caller owns the returned document.
    /// </summary>
    Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a GET and deserialises the named collection property out of the
    /// envelope, e.g. "alerts" from {"status":"ok","alerts":[...]}.
    /// </summary>
    Task<IReadOnlyList<T>> GetCollectionAsync<T>(
        string relativeUrl,
        string collectionProperty,
        CancellationToken cancellationToken = default);
}
