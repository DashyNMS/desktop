namespace DesktopNMS.Core.Api;

/// <summary>
/// Everything needed to talk to one LibreNMS instance.
/// </summary>
public sealed class LibreNmsConnection
{
    public const string ApiPathSegment = "api/v0";

    public LibreNmsConnection(Uri webRoot, string apiToken, bool allowUntrustedCertificate = false, int timeoutSeconds = 30)
    {
        WebRoot = webRoot ?? throw new ArgumentNullException(nameof(webRoot));
        ApiToken = apiToken ?? throw new ArgumentNullException(nameof(apiToken));
        AllowUntrustedCertificate = allowUntrustedCertificate;
        TimeoutSeconds = timeoutSeconds < 5 ? 5 : timeoutSeconds;
    }

    /// <summary>Root of the LibreNMS web UI, always with a trailing slash.</summary>
    public Uri WebRoot { get; }

    /// <summary>Base address for API calls, i.e. <see cref="WebRoot"/> + "api/v0/".</summary>
    public Uri ApiBase => new(WebRoot, ApiPathSegment + "/");

    public string ApiToken { get; }

    /// <summary>Accept self-signed / mismatched certificates. Off by default.</summary>
    public bool AllowUntrustedCertificate { get; }

    public int TimeoutSeconds { get; }

    /// <summary>
    /// Turns whatever the user typed into a usable web root: adds a scheme if
    /// missing, strips a trailing "/api/v0" if they pasted the API URL, and
    /// guarantees a trailing slash so relative URIs resolve correctly.
    /// </summary>
    public static bool TryParseWebRoot(string? input, out Uri? webRoot, out string? error)
    {
        webRoot = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the address of your LibreNMS server.";
            return false;
        }

        var text = input.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            error = "That is not a valid URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "The address must start with http:// or https://.";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');

        // Be forgiving if the user pasted the API endpoint rather than the site root.
        if (path.EndsWith("/" + ApiPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^(ApiPathSegment.Length + 1)];
        }
        else if (path.Equals("/" + ApiPathSegment, StringComparison.OrdinalIgnoreCase))
        {
            path = string.Empty;
        }

        var builder = new UriBuilder(parsed)
        {
            Path = path + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        webRoot = builder.Uri;
        return true;
    }

    /// <summary>Absolute URL of an alert in the LibreNMS web UI.</summary>
    public Uri AlertUrl(int alertId) => new(WebRoot, $"alerts/?state=-1&alert_id={alertId}");

    /// <summary>Absolute URL of a device's overview page in the LibreNMS web UI.</summary>
    public Uri DeviceUrl(int deviceId) => new(WebRoot, $"device/device={deviceId}/");
}
