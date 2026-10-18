using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DesktopNMS.Core.Intelligence;

/// <summary>What an ID token says about the account, once it's been checked.</summary>
public sealed record IdTokenClaims(string Subject, string? Email);

/// <summary>
/// Checks an OpenAI ID token: an RS256 signature by a key in OpenAI's
/// published JWKS (found through its OpenID configuration), the issuer, the
/// audience (the issued client ID), expiry and, on sign-in, the nonce.
/// </summary>
internal sealed class IdTokenValidator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private JsonArray? _keys;

    public IdTokenValidator(HttpClient http)
    {
        _http = http;
    }

    public async Task<IdTokenClaims> ValidateAsync(string token, string issuer, string audience, string? expectedNonce, CancellationToken cancellationToken)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new ChatGptSignInException("ChatGPT's ID token wasn't valid.");
        }

        var header = JsonNode.Parse(FromBase64Url(parts[0])) as JsonObject;
        var payload = JsonNode.Parse(FromBase64Url(parts[1])) as JsonObject;
        if (header is null || payload is null || header["alg"]?.GetValue<string>() != "RS256")
        {
            throw new ChatGptSignInException("ChatGPT's ID token wasn't valid.");
        }

        var kid = header["kid"]?.GetValue<string>();
        var key = await FindKeyAsync(issuer, kid, cancellationToken).ConfigureAwait(false)
                  ?? await FindKeyAsync(issuer, kid, cancellationToken, refresh: true).ConfigureAwait(false)
                  ?? throw new ChatGptSignInException("ChatGPT's ID token was signed with a key DashyNMS doesn't recognise.");

        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = FromBase64Url(key["n"]!.GetValue<string>()),
            Exponent = FromBase64Url(key["e"]!.GetValue<string>()),
        });

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            throw new ChatGptSignInException("ChatGPT's ID token signature didn't check out.");
        }

        if (payload["iss"]?.GetValue<string>()?.TrimEnd('/') != issuer.TrimEnd('/'))
        {
            throw new ChatGptSignInException("ChatGPT's ID token came from an unexpected issuer.");
        }

        var audiences = payload["aud"] switch
        {
            JsonArray array => array.Select(a => a?.GetValue<string>()).ToList(),
            JsonValue value => new List<string?> { value.GetValue<string>() },
            _ => new List<string?>(),
        };
        if (!audiences.Contains(audience))
        {
            throw new ChatGptSignInException("ChatGPT's ID token was for a different app.");
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds((long)(payload["exp"]?.GetValue<double>() ?? 0));
        if (expires + ClockSkew < DateTimeOffset.UtcNow)
        {
            throw new ChatGptSignInException("ChatGPT's ID token has expired. Sign in again.");
        }

        if (expectedNonce is not null && payload["nonce"]?.GetValue<string>() != expectedNonce)
        {
            throw new ChatGptSignInException("The sign-in response didn't match this attempt, so it was rejected.");
        }

        var subject = payload["sub"]?.GetValue<string>();
        if (string.IsNullOrEmpty(subject))
        {
            throw new ChatGptSignInException("ChatGPT's ID token didn't name the account.");
        }

        return new IdTokenClaims(subject, payload["email"]?.GetValue<string>());
    }

    private async Task<JsonObject?> FindKeyAsync(string issuer, string? kid, CancellationToken cancellationToken, bool refresh = false)
    {
        if (_keys is null || refresh)
        {
            var configuration = JsonNode.Parse(await _http.GetStringAsync(issuer.TrimEnd('/') + "/.well-known/openid-configuration", cancellationToken).ConfigureAwait(false));
            var jwksUri = configuration?["jwks_uri"]?.GetValue<string>() ?? throw new ChatGptSignInException("Couldn't read OpenAI's sign-in keys.");
            _keys = JsonNode.Parse(await _http.GetStringAsync(jwksUri, cancellationToken).ConfigureAwait(false))?["keys"] as JsonArray;
        }

        return _keys?
            .OfType<JsonObject>()
            .FirstOrDefault(k => k["kty"]?.GetValue<string>() == "RSA" && (kid is null || k["kid"]?.GetValue<string>() == kid));
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
