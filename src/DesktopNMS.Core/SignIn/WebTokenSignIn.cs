using System.Globalization;
using System.Text.Json;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Core.SignIn;

/// <summary>What to do after a page of LibreNMS's website has loaded in the sign-in web view.</summary>
public enum WebSignInAction
{
    /// <summary>Leave the page to the user: a login, two-factor or single sign-on page.</summary>
    Wait,

    /// <summary>Signed in but somewhere else (the dashboard): go to the API Tokens page.</summary>
    GoToTokensPage,

    /// <summary>On the API Tokens page: fill in and submit its Create form.</summary>
    Submit,

    /// <summary>The page shows the new token: <see cref="WebSignInStep.Token"/>.</summary>
    Done,

    /// <summary>
    /// The API Tokens page is there but has no Create form: the account lacks
    /// API access, or LibreNMS is older than 26.4.
    /// </summary>
    NotAllowed,

    /// <summary>The Create form came back without a token.</summary>
    Failed,
}

/// <summary>One decision of <see cref="WebTokenSignIn.Next"/>, with the token once there is one.</summary>
public sealed record WebSignInStep(WebSignInAction Action, string? Token = null);

/// <summary>
/// "Sign in with LibreNMS" (DashyNMS/mobile#162), shared by both apps: the
/// user signs in on LibreNMS's own website in a web view - however their
/// server signs people in, two-factor and single sign-on included - and the
/// app then creates its own API token on the API Tokens page and reads it
/// back, so nobody copies a token by hand. LibreNMS has no API call that
/// makes a token (an API call needs one already), so the page is the only way.
/// </summary>
/// <remarks>
/// <para>The page is LibreNMS 26.4's <c>resources/views/user/api-access.blade.php</c>.
/// Only two of its element ids are relied on - the form
/// <c>#create-api-token-form</c> and the read-only <c>#api-token-once</c> the
/// new token appears in once - so a restyle of the page doesn't break this.
/// Older pages have neither, and end up as <see cref="WebSignInAction.NotAllowed"/>,
/// as does an account without the <c>api.access</c> permission (only admins
/// have it by default).</para>
/// <para>Kept apart from the web view so each step can be tested: each app's
/// web view runs <see cref="ProbeScript"/> after every load, and
/// <see cref="Next"/> turns where it is and what the probe found into what to do.</para>
/// <para>Scripts only ever run on the server's own pages (same scheme, host
/// and port): a single sign-on provider's pages are left alone.</para>
/// </remarks>
public sealed class WebTokenSignIn
{
    /// <summary>LibreNMS's path for the API Tokens page, under the web root.</summary>
    public const string TokensPath = "api-access";

    /// <summary>
    /// Says what the loaded page is: <c>token:…</c> once the new token is
    /// shown, <c>form</c> for the API Tokens page, <c>login</c> for a page
    /// asking for a password, <c>signed-in</c> for any other page with
    /// LibreNMS's menu (it has a log-out link), and <c>other</c> otherwise.
    /// </summary>
    public const string ProbeScript =
        "(function(){" +
        "var t=document.getElementById('api-token-once');if(t&&t.value)return 'token:'+t.value;" +
        "if(document.getElementById('create-api-token-form'))return 'form';" +
        "if(document.querySelector('input[type=password]'))return 'login';" +
        "if(document.querySelector('form[action$=\"/logout\"],a[href$=\"/logout\"]'))return 'signed-in';" +
        "return 'other';})()";

    // Each page gets sent there at most this often, so a server that keeps
    // redirecting away (a policy, a broken install) can't loop for ever.
    private const int MaxTokensPageVisits = 3;

    private readonly Uri _webRoot;
    private bool _submitted;
    private int _visits;

    public WebTokenSignIn(Uri webRoot, string tokenName)
    {
        _webRoot = webRoot ?? throw new ArgumentNullException(nameof(webRoot));
        TokenName = tokenName;
    }

    /// <summary>Where the web view starts: LibreNMS sends it to log in first, then back here.</summary>
    public Uri TokensPage => new(_webRoot, TokensPath);

    /// <summary>What the token is called in LibreNMS's list - see <see cref="NameFor"/>.</summary>
    public string TokenName { get; }

    /// <summary>
    /// Fills in the Create form's description and submits it. Expiry is left
    /// blank (never), as a pasted token would be; the description says which
    /// device it's for, so it's easy to find and revoke.
    /// </summary>
    public string SubmitScript =>
        "(function(){var f=document.getElementById('create-api-token-form');if(!f)return 'no-form';" +
        "var d=f.querySelector('[name=description]');if(d)d.value=" + JsonSerializer.Serialize(TokenName) + ";" +
        "var e=f.querySelector('[name=expires_in]');if(e)e.value='';" +
        "f.submit();return 'submitted';})()";

    /// <summary>
    /// "DashyNMS · Tom's iPhone · 7 Oct 2026", or the PC's name on desktop:
    /// the device and the day, so the user can tell tokens apart and revoke
    /// an old device's. LibreNMS allows 255 characters.
    /// </summary>
    public static string NameFor(string? deviceName, DateTime date)
    {
        var device = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim();
        var name = "DashyNMS · " + device + " · " + date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return name.Length <= 255 ? name : name[..255];
    }

    /// <summary>
    /// Whether a certificate presented by <paramref name="host"/> is the
    /// server's - the only one the web view may accept on the strength of a
    /// fingerprint the user trusted.
    /// </summary>
    public bool IsServerHost(string? host) =>
        string.Equals(host, _webRoot.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="location"/> is on the server itself - same scheme, host and port.</summary>
    public bool IsOnServer(Uri? location) =>
        location is not null
        && string.Equals(location.Scheme, _webRoot.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(location.Host, _webRoot.Host, StringComparison.OrdinalIgnoreCase)
        && location.Port == _webRoot.Port;

    /// <summary>
    /// What to do now the web view is at <paramref name="location"/> and
    /// <see cref="ProbeScript"/> answered <paramref name="probe"/> (null when
    /// it wasn't run: another site's page).
    /// </summary>
    public WebSignInStep Next(Uri? location, string? probe)
    {
        if (!IsOnServer(location))
        {
            return new(WebSignInAction.Wait);
        }

        var answer = Unwrap(probe);

        if (answer.StartsWith("token:", StringComparison.Ordinal))
        {
            var token = answer["token:".Length..].Trim();
            return ApiTokenText.IsToken(token) ? new(WebSignInAction.Done, token) : new(WebSignInAction.Failed);
        }

        var onTokensPage = location!.AbsolutePath.TrimEnd('/')
            .EndsWith("/" + TokensPath, StringComparison.OrdinalIgnoreCase);

        switch (answer)
        {
            case "form":
                if (_submitted)
                {
                    // Back on the form with no token: LibreNMS turned it down.
                    return new(WebSignInAction.Failed);
                }

                _submitted = true;
                return new(WebSignInAction.Submit);

            case "login":
                return new(WebSignInAction.Wait);

            case "signed-in" or "other" when onTokensPage:
                return new(WebSignInAction.NotAllowed);

            case "signed-in" when _visits < MaxTokensPageVisits:
                _visits++;
                return new(WebSignInAction.GoToTokensPage);

            default:
                // Two-factor, an error page, or a server that won't let go.
                return new(WebSignInAction.Wait);
        }
    }

    /// <summary>
    /// A script's result as plain text. Android and WebView2 hand results back
    /// JSON-encoded (<c>"form"</c>, quotes included), iOS as they are.
    /// </summary>
    public static string Unwrap(string? result)
    {
        var text = result?.Trim() ?? string.Empty;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(text) ?? string.Empty;
            }
            catch (JsonException)
            {
                return text[1..^1];
            }
        }

        return text == "null" ? string.Empty : text;
    }
}
