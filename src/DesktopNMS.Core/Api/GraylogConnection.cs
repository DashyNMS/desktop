using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Everything needed to talk to one Graylog server, built the way LibreNMS's
/// own client builds it (<c>app/ApiClients/GraylogApi.php</c>): the server
/// address plus an optional port, an "/api" prefix for Graylog 2.1 or newer,
/// and HTTP basic auth.
/// </summary>
public sealed class GraylogConnection
{
    public GraylogConnection(
        Uri root,
        string version,
        string? baseUri,
        string username,
        string password,
        bool allowUntrustedCertificate = false,
        int timeoutSeconds = 30)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Version = string.IsNullOrWhiteSpace(version) ? GraylogSettings.Version21 : version;
        BaseUri = string.IsNullOrWhiteSpace(baseUri) ? null : baseUri.Trim();
        Username = username ?? throw new ArgumentNullException(nameof(username));
        Password = password ?? throw new ArgumentNullException(nameof(password));
        AllowUntrustedCertificate = allowUntrustedCertificate;
        TimeoutSeconds = timeoutSeconds < 5 ? 5 : timeoutSeconds;
    }

    /// <summary>The server address with the port applied, always with a trailing slash - every API path is relative to this.</summary>
    public Uri Root { get; }

    public string Version { get; }

    public string? BaseUri { get; }

    public string Username { get; }

    public string Password { get; }

    public bool AllowUntrustedCertificate { get; }

    public int TimeoutSeconds { get; }

    /// <summary>
    /// "api/" for Graylog 2.1 or newer, nothing otherwise. LibreNMS decides
    /// this with <c>version_compare($version, '2.1', '>=')</c>, under which
    /// "other" sorts below every number - so "other" gets no prefix either,
    /// and only its search path is overridden (by <see cref="BaseUri"/>).
    /// </summary>
    public string ApiPrefix => Version == GraylogSettings.Version21 ? "api/" : string.Empty;

    /// <summary>Relative path of the streams list.</summary>
    public string StreamsPath => ApiPrefix + "streams";

    /// <summary>
    /// Relative path of the search endpoint - <see cref="BaseUri"/> for
    /// "other", otherwise the standard relative-time search. LibreNMS only
    /// shows its Base URI field for "other" but uses a leftover value with any
    /// version; here it only applies to "other", so switching version away
    /// never leaves a hidden override behind.
    /// </summary>
    public string SearchPath => BaseUri is { } custom && Version == GraylogSettings.VersionOther
        ? custom.TrimStart('/')
        : ApiPrefix + "search/universal/relative";

    /// <summary>
    /// A connection for the given settings and password, or null with the
    /// reason when they aren't complete enough to connect with. Doesn't look
    /// at <see cref="GraylogSettings.Enabled"/> - the caller decides whether
    /// a disabled integration should connect (Settings' Test button does).
    /// </summary>
    public static GraylogConnection? FromSettings(GraylogSettings settings, string? password, out string? error)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryParseRoot(settings.Server, settings.Port, out var root, out error) || root is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            error = "Enter a Graylog username (or an access token).";
            return null;
        }

        if (string.IsNullOrEmpty(password))
        {
            error = "Enter a Graylog password (or \"token\" when the username is an access token).";
            return null;
        }

        if (settings.Version == GraylogSettings.VersionOther && string.IsNullOrWhiteSpace(settings.BaseUri))
        {
            error = "With the version set to Other, enter the search API's base URI.";
            return null;
        }

        return new GraylogConnection(root, settings.Version, settings.BaseUri, settings.Username.Trim(), password, settings.AllowUntrustedCertificate);
    }

    /// <summary>
    /// Builds <see cref="Root"/> from LibreNMS-style settings: a server
    /// address (a scheme is added if missing) and an optional port that
    /// replaces any port in the address. A path on the address is kept, so a
    /// Graylog behind a reverse proxy at https://host/graylog works too.
    /// </summary>
    public static bool TryParseRoot(string? server, int? port, out Uri? root, out string? error)
    {
        root = null;
        error = null;

        if (string.IsNullOrWhiteSpace(server))
        {
            error = "Enter the address of your Graylog server.";
            return false;
        }

        if (port is { } p && (p < 1 || p > 65535))
        {
            error = "The port must be between 1 and 65535.";
            return false;
        }

        var text = server.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "That is not a valid address.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "The address must start with http:// or https://.";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');

        // A pasted API address ("https://graylog:9000/api") would otherwise
        // end up as ".../api/api/streams".
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var builder = new UriBuilder(parsed)
        {
            Path = path + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        if (port is { } explicitPort)
        {
            builder.Port = explicitPort;
        }

        root = builder.Uri;
        return true;
    }
}
