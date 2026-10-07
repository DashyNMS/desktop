using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Api;

/// <summary>
/// What a LibreNMS API token looks like - for "Sign in with LibreNMS"
/// reading one off the API Tokens page, and for a token pasted or copied
/// (#261). One helper, so both apps accept exactly the same tokens.
/// </summary>
/// <remarks>
/// LibreNMS 26.9 moved tokens to Laravel Sanctum, which issues
/// <c>{id}|{secret}</c> (the secret may start with a configured prefix such
/// as <c>librenms_</c>) and still accepts the older 32 hex characters.
/// </remarks>
public static partial class ApiTokenText
{
    /// <summary>Whether <paramref name="text"/> is exactly a token, nothing around it.</summary>
    public static bool IsToken(string? text) => !string.IsNullOrEmpty(text) && TokenPattern().IsMatch(text);

    /// <summary>
    /// The token in what was pasted or copied: trimmed, and without a leading
    /// <c>Bearer </c> (as an Authorization header was copied). False when
    /// what's left isn't a token.
    /// </summary>
    public static bool TryExtract(string? text, out string token)
    {
        var candidate = text?.Trim() ?? string.Empty;
        if (candidate.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["Bearer ".Length..].Trim();
        }

        token = IsToken(candidate) ? candidate : string.Empty;
        return token.Length > 0;
    }

    [GeneratedRegex(@"^(?:\d+\|[A-Za-z0-9_\-]{20,}|[A-Fa-f0-9]{32})$")]
    private static partial Regex TokenPattern();
}
