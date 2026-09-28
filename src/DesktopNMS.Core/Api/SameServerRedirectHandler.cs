using System.Net;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Follows redirects itself, so the LibreNMS API token only ever goes back
/// to the server it was meant for.
/// </summary>
/// <remarks>
/// The token travels as <c>X-Auth-Token</c>, a custom header. When
/// <see cref="SocketsHttpHandler"/> follows a redirect itself it drops the
/// standard <c>Authorization</c> header but carries every other header along,
/// so a 302 to another host (a misconfigured proxy, a compromised plugin, or
/// anyone who can tamper with plain http) was handed the token (mobile
/// issue #1). Here a redirect keeps the token only while it stays on the same
/// server: the same host, with the same scheme and port, or moving from http
/// to https (a server upgrading its own plain-http address). Anywhere else, the
/// request goes on without the token, and the server there answers as it
/// would to anyone.
/// <para>
/// Otherwise it behaves as .NET's own redirect handling: at most
/// <see cref="MaxRedirects"/> hops, never from https down to http, and a
/// 301/302 after a POST, or any 303, becomes a GET with no body.
/// </para>
/// </remarks>
internal sealed class SameServerRedirectHandler : DelegatingHandler
{
    /// <summary>.NET's own default (<c>MaxAutomaticRedirections</c>).</summary>
    internal const int MaxRedirects = 50;

    internal const string TokenHeader = "X-Auth-Token";

    /// <param name="inner">A handler with <c>AllowAutoRedirect</c> off - this one does the following.</param>
    public SameServerRedirectHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var origin = request.RequestUri!;
        var current = request;
        var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);

        for (var hops = 0; hops < MaxRedirects && IsRedirect(response.StatusCode); hops++)
        {
            if (Target(current.RequestUri!, response.Headers.Location) is not { } target)
            {
                break;
            }

            // The hops' request messages aren't disposed: that would dispose
            // the body a 307/308 sends on again.
            var next = Follow(current, target, response.StatusCode, SameServer(origin, target));
            response.Dispose();
            current = next;
            response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is still the server the token belongs
    /// to - the same host (and port, for the same scheme), or the same host
    /// upgraded from http to its https default.
    /// </summary>
    internal static bool SameServer(Uri origin, Uri target)
    {
        if (!string.Equals(origin.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (origin.Scheme == target.Scheme)
        {
            return origin.Port == target.Port;
        }

        return origin.Scheme == Uri.UriSchemeHttp && target.Scheme == Uri.UriSchemeHttps && target.IsDefaultPort;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The absolute address to go to, or null to stop and hand back the redirect as it is.</summary>
    private static Uri? Target(Uri from, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        // Off Windows, "/api/v0/..." can come back as an absolute file: URI
        // (a Unix path); it's a path on the same server.
        if (location.IsAbsoluteUri && location.IsFile && location.OriginalString.StartsWith('/'))
        {
            location = new Uri(location.OriginalString, UriKind.Relative);
        }

        var target = location.IsAbsoluteUri ? location : new Uri(from, location);

        // Only web addresses, and never from https down to http, as .NET.
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (from.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
        {
            return null;
        }

        // A fragment on the Location wins; otherwise the original one carries over.
        if (string.IsNullOrEmpty(target.Fragment) && !string.IsNullOrEmpty(from.Fragment))
        {
            target = new UriBuilder(target) { Fragment = from.Fragment.TrimStart('#') }.Uri;
        }

        return target;
    }

    private static HttpRequestMessage Follow(HttpRequestMessage from, Uri target, HttpStatusCode status, bool keepToken)
    {
        var becomesGet = status == HttpStatusCode.SeeOther
            ? from.Method != HttpMethod.Head
            : status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && from.Method == HttpMethod.Post;

        var next = new HttpRequestMessage(becomesGet ? HttpMethod.Get : from.Method, target)
        {
            Version = from.Version,
            VersionPolicy = from.VersionPolicy,
            Content = becomesGet ? null : from.Content,
        };

        foreach (var header in from.Headers)
        {
            if (header.Key.Equals(TokenHeader, StringComparison.OrdinalIgnoreCase) && !keepToken)
            {
                continue;
            }

            // Authorization never follows a redirect, as .NET's own handling.
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in from.Options)
        {
            ((IDictionary<string, object?>)next.Options)[option.Key] = option.Value;
        }

        return next;
    }
}
