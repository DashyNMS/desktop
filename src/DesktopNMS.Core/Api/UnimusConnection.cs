namespace DesktopNMS.Core.Api;

/// <summary>Everything needed to talk to one Unimus instance - mirrors <see cref="LibreNmsConnection"/>'s shape for an unrelated service.</summary>
public sealed class UnimusConnection
{
    public const string ApiPathSegment = "api/v2";

    public UnimusConnection(Uri webRoot, string apiToken, bool allowUntrustedCertificate = false, int timeoutSeconds = 30)
    {
        WebRoot = webRoot ?? throw new ArgumentNullException(nameof(webRoot));
        ApiToken = apiToken ?? throw new ArgumentNullException(nameof(apiToken));
        AllowUntrustedCertificate = allowUntrustedCertificate;
        TimeoutSeconds = timeoutSeconds < 5 ? 5 : timeoutSeconds;
    }

    /// <summary>Root of the Unimus instance, always with a trailing slash.</summary>
    public Uri WebRoot { get; }

    /// <summary>Base address for API calls, i.e. <see cref="WebRoot"/> + "api/v2/".</summary>
    public Uri ApiBase => new(WebRoot, ApiPathSegment + "/");

    public string ApiToken { get; }

    /// <summary>Accept self-signed / internally-issued certificates - common for an internal-only tool like Unimus. Off by default.</summary>
    public bool AllowUntrustedCertificate { get; }

    public int TimeoutSeconds { get; }

    /// <summary>Same forgiving parse as <see cref="LibreNmsConnection.TryParseWebRoot"/> - adds a scheme if missing, strips a trailing "/api/v2" if the API URL was pasted, guarantees a trailing slash.</summary>
    public static bool TryParseWebRoot(string? input, out Uri? webRoot, out string? error)
    {
        webRoot = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter the address of your Unimus server.";
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
}
