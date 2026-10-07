namespace DesktopNMS.Core.SignIn;

/// <summary>
/// Where a LibreNMS API token comes from, worded once for both apps (#261).
/// Checked against LibreNMS's <c>resources/views/layouts/menu.blade.php</c>:
/// the cog (settings) menu has API, then API Tokens - "API Settings" before
/// 26.4 - and only for accounts with the <c>api.access</c> permission, which
/// only admins have by default.
/// </summary>
public static class SignInHelp
{
    /// <summary>The hint beside the token box.</summary>
    public const string TokenHint = "In LibreNMS, open the settings menu (the cog), then API, then API Tokens, and create a token. No API there? A LibreNMS admin can give your account API access.";

    /// <summary>Signing in with the token box empty and no token saved.</summary>
    public const string MissingToken = "Enter an API token from LibreNMS: the settings menu (the cog), then API, then API Tokens.";
}
