using System.Net;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Raised when Graylog returns a non-success HTTP status or a body that
/// cannot be parsed. Mirrors <see cref="LibreNmsApiException"/>'s shape -
/// kept as a separate type since the two APIs are unrelated services with
/// unrelated error envelopes.
/// </summary>
public sealed class GraylogApiException : Exception
{
    public GraylogApiException(
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>True when the username or password was rejected, so Settings can flag it rather than just showing a generic failure.</summary>
    public bool IsAuthenticationFailure =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public string ToUserMessage() =>
        StatusCode is null ? Message : $"{Message} (HTTP {(int)StatusCode})";
}
