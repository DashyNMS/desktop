using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopNMS.Core.Intelligence;

/// <summary>
/// "Continue with ChatGPT": OpenAI's Sign in with ChatGPT for local apps,
/// with plan usage. Authorization code + PKCE through the system browser,
/// back to a loopback listener on 127.0.0.1, then a code exchange - no client
/// secret. The first sign-in registers this installation
/// (client_id=dynamic_agent_client, its host ID and the app's name) and
/// OpenAI issues a client ID that every later sign-in and refresh uses.
/// </summary>
/// <remarks>
/// Protocol per developers.openai.com/siwc/token-sharing-open-source/sign-in.
/// Refresh isn't documented there; it's the standard OAuth refresh_token
/// grant against the same token endpoint, and a failed refresh means signing
/// in again.
/// </remarks>
public sealed class ChatGptSignIn
{
    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string RegistrationClientId = "dynamic_agent_client";
    public const string PlanUsageScope = "chatgpt.tokens.use.direct";
    public const string Scopes = "openid profile email offline_access resource.invoke " + PlanUsageScope;
    public const string CallbackPath = "/auth/callback";

    /// <summary>The documented example port, tried first; any free port works as long as it's the same for the whole attempt.</summary>
    private const int PreferredPort = 1455;

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _appName;
    private readonly IdTokenValidator _validator;

    public ChatGptSignIn(HttpClient http, string appName)
    {
        _http = http;
        _appName = appName;
        _validator = new IdTokenValidator(http);
    }

    /// <summary>
    /// Signs in: opens <paramref name="openBrowser"/> on OpenAI's page and
    /// waits for the browser to come back. Returns the new credentials, or
    /// throws <see cref="ChatGptSignInException"/> with a reason a person can read.
    /// </summary>
    public async Task<ChatGptCredentials> SignInAsync(ChatGptCredentials? existing, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        var hostId = string.IsNullOrEmpty(existing?.HostId) ? "urn:uuid:" + Guid.NewGuid() : existing!.HostId;
        var issuedClientId = existing?.ClientId;
        var registering = string.IsNullOrEmpty(issuedClientId);

        var verifier = RandomUrlSafe(32);
        var state = RandomUrlSafe(16);
        var nonce = RandomUrlSafe(16);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Listening before the browser opens, so the callback can't arrive first.
        using var listener = StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";

        var query = new List<(string Key, string Value)>
        {
            ("client_id", registering ? RegistrationClientId : issuedClientId!),
            ("ext_agent_host_id", hostId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("scope", Scopes),
            ("resource", Resource),
            ("state", state),
            ("nonce", nonce),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
        };

        if (registering)
        {
            query.Add(("agent_name_hint", _appName));
        }
        else
        {
            if (!string.IsNullOrEmpty(existing!.IdToken))
            {
                query.Add(("id_token_hint", existing.IdToken!));
            }

            if (!string.IsNullOrEmpty(existing.Email))
            {
                query.Add(("login_hint", existing.Email!));
            }
        }

        var authorizeUrl = new Uri(AuthorizeEndpoint + "?" + string.Join("&", query.Select(q => q.Key + "=" + Uri.EscapeDataString(q.Value))));
        openBrowser(authorizeUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);

        Dictionary<string, string> callback;
        try
        {
            callback = await WaitForCallbackAsync(listener, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatGptSignInException("Signing in took too long. Try again.");
        }

        if (!callback.TryGetValue("state", out var returnedState) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(returnedState), Encoding.ASCII.GetBytes(state)))
        {
            throw new ChatGptSignInException("The sign-in response didn't match this attempt, so it was ignored. Try again.");
        }

        if (callback.TryGetValue("error", out var error))
        {
            throw new ChatGptSignInException(error == "access_denied" ? "Signing in with ChatGPT was cancelled." : "ChatGPT sign-in failed (" + error + ").");
        }

        if (!callback.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            throw new ChatGptSignInException("ChatGPT didn't return a sign-in code. Try again.");
        }

        // The issued client ID comes back on registration; on a later
        // sign-in it may be left out, but must never change.
        callback.TryGetValue("client_id", out var returnedClientId);
        string clientId;
        if (registering)
        {
            clientId = !string.IsNullOrEmpty(returnedClientId) ? returnedClientId! : throw new ChatGptSignInException("ChatGPT didn't register DashyNMS. Try again.");
        }
        else
        {
            if (!string.IsNullOrEmpty(returnedClientId) && returnedClientId != issuedClientId)
            {
                throw new ChatGptSignInException("ChatGPT answered for a different app registration, so the sign-in was rejected.");
            }

            clientId = issuedClientId!;
        }

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["resource"] = Resource,
        }, cancellationToken).ConfigureAwait(false);

        // A fresh record: signing in as another account never inherits the
        // last one's tokens or identity.
        var credentials = new ChatGptCredentials { HostId = hostId, ClientId = clientId };
        await ApplyTokensAsync(credentials, tokens, nonce, cancellationToken).ConfigureAwait(false);
        return credentials;
    }

    /// <summary>Renews the access token. A failure means signing in again.</summary>
    public async Task RefreshAsync(ChatGptCredentials credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(credentials.RefreshToken) || string.IsNullOrEmpty(credentials.ClientId))
        {
            throw new ChatGptSignInException("Sign in with ChatGPT again.");
        }

        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credentials.ClientId!,
            ["refresh_token"] = credentials.RefreshToken!,
            ["resource"] = Resource,
        }, cancellationToken).ConfigureAwait(false);

        await ApplyTokensAsync(credentials, tokens, expectedNonce: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyTokensAsync(ChatGptCredentials credentials, JsonObject tokens, string? expectedNonce, CancellationToken cancellationToken)
    {
        var access = tokens["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(access))
        {
            throw new ChatGptSignInException("ChatGPT didn't return an access token.");
        }

        if (tokens["id_token"]?.GetValue<string>() is { Length: > 0 } idToken)
        {
            var claims = await _validator.ValidateAsync(idToken, Issuer, credentials.ClientId!, expectedNonce, cancellationToken).ConfigureAwait(false);
            if (credentials.Subject is { } subject && claims.Subject != subject)
            {
                throw new ChatGptSignInException("ChatGPT renewed the sign-in for a different account, so it was rejected.");
            }

            credentials.IdToken = idToken;
            credentials.Subject = claims.Subject;
            credentials.Email = claims.Email ?? credentials.Email;
        }
        else if (expectedNonce is not null)
        {
            throw new ChatGptSignInException("ChatGPT didn't return an ID token.");
        }

        // A refresh token rotates: the old one is spent, so all of these
        // are replaced together.
        credentials.AccessToken = access;
        credentials.RefreshToken = tokens["refresh_token"]?.GetValue<string>() ?? credentials.RefreshToken;
        var expiresIn = tokens["expires_in"]?.GetValue<double>() ?? 3600;
        credentials.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        if (tokens["scope"]?.GetValue<string>() is { } scope)
        {
            credentials.Scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }

    private async Task<JsonObject> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || json is null)
        {
            var code = json?["error"]?.GetValue<string>();
            throw new ChatGptSignInException(code == "invalid_grant"
                ? "The ChatGPT sign-in has expired. Sign in again."
                : $"ChatGPT sign-in failed (HTTP {(int)response.StatusCode}{(code is null ? string.Empty : ", " + code)}).");
        }

        return json;
    }

    private static TcpListener StartListener()
    {
        foreach (var port in new[] { PreferredPort, 0 })
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return listener;
            }
            catch (SocketException) when (port != 0)
            {
            }
        }

        throw new ChatGptSignInException("Couldn't start listening for the sign-in.");
    }

    /// <summary>The browser's one request to the callback: its query, and a page telling the person they can go back to DashyNMS.</summary>
    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;

            // GET /auth/callback?code=…&state=… HTTP/1.1
            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : string.Empty;
            var path = target.Split('?', 2)[0];
            if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not found.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var query = target.Contains('?') ? target.Split('?', 2)[1] : string.Empty;
            var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

            var ok = values.ContainsKey("code");
            await WriteResponseAsync(stream, 200, ok
                ? "You're signed in. You can close this tab and go back to DashyNMS."
                : "Signing in didn't finish. You can close this tab and go back to DashyNMS.", cancellationToken).ConfigureAwait(false);
            return values;
        }
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string message, CancellationToken cancellationToken)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>DashyNMS</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;margin:3em\"><p>" + WebUtility.HtmlEncode(message) + "</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ChatGptSignInException : Exception
{
    public ChatGptSignInException(string message)
        : base(message)
    {
    }
}
