using System.Net;
using DesktopNMS.Core.Security;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Raised when LibreNMS returns a non-success HTTP status, a
/// <c>{"status":"error"}</c> envelope, or a body that cannot be parsed.
/// </summary>
public sealed class LibreNmsApiException : Exception
{
    public LibreNmsApiException(
        string message,
        HttpStatusCode? statusCode = null,
        string? serverMessage = null,
        Exception? innerException = null,
        bool looksLikeStaleConnection = false)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
        LooksLikeStaleConnection = looksLikeStaleConnection;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Set when the connection failed only because the server's certificate
    /// isn't one the user has trusted yet, or has changed since they did,
    /// with "Allow untrusted certificate" on - so the app can show it and ask (#189).
    /// </summary>
    public CertificateDetails? UntrustedCertificate { get; init; }

    /// <summary>The "message" field from the LibreNMS error envelope, when present.</summary>
    public string? ServerMessage { get; }

    /// <summary>
    /// True when the underlying failure looks like a pooled connection that
    /// the server had already closed, rather than a genuinely slow response or
    /// a real network-down condition. The transport uses this to decide
    /// whether a single automatic retry is worth attempting.
    /// </summary>
    public bool LooksLikeStaleConnection { get; }

    /// <summary>
    /// True when the token itself was rejected (401), so the UI can send the
    /// user back to sign-in. A 403 is not this: the token is fine, its user
    /// just isn't allowed that one thing (#51) - see <see cref="IsPermissionDenied"/>.
    /// </summary>
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized;

    /// <summary>The token was accepted, but its user isn't allowed this action (403) - see <see cref="ApiPermissions"/>.</summary>
    public bool IsPermissionDenied => StatusCode is HttpStatusCode.Forbidden;

    /// <summary>What a 403 means, in place of LibreNMS's own "This action is unauthorized."</summary>
    public const string PermissionDeniedMessage =
        "Your API token isn't allowed to do this in LibreNMS. A LibreNMS admin can give its user more permissions.";

    /// <summary>A single line suitable for a status bar or a toast.</summary>
    public string ToUserMessage()
    {
        if (IsPermissionDenied)
        {
            return PermissionDeniedMessage;
        }

        if (!string.IsNullOrWhiteSpace(ServerMessage))
        {
            return ServerMessage!;
        }

        return StatusCode is null ? Message : $"{Message} (HTTP {(int)StatusCode})";
    }
}
