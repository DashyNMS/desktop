using System.Net;

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

    /// <summary>The "message" field from the LibreNMS error envelope, when present.</summary>
    public string? ServerMessage { get; }

    /// <summary>
    /// True when the underlying failure looks like a pooled connection that
    /// the server had already closed, rather than a genuinely slow response or
    /// a real network-down condition. The transport uses this to decide
    /// whether a single automatic retry is worth attempting.
    /// </summary>
    public bool LooksLikeStaleConnection { get; }

    /// <summary>True when the token was rejected, so the UI can send the user back to sign-in.</summary>
    public bool IsAuthenticationFailure =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>A single line suitable for a status bar or a toast.</summary>
    public string ToUserMessage()
    {
        if (!string.IsNullOrWhiteSpace(ServerMessage))
        {
            return ServerMessage!;
        }

        return StatusCode is null ? Message : $"{Message} (HTTP {(int)StatusCode})";
    }
}
