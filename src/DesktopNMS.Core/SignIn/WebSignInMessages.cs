namespace DesktopNMS.Core.SignIn;

/// <summary>
/// What both apps say when "Sign in with LibreNMS" can't go ahead - kept
/// here so the phone and the PC word it the same way.
/// </summary>
public static class WebSignInMessages
{
    public const string NeedsHttps = "Signing in with LibreNMS needs an https:// address. Use an API token for this server instead.";

    public const string NotAllowed = "Your LibreNMS account can't create API tokens here. It needs API access from a LibreNMS admin, and LibreNMS 26.4 or later. You can paste an API token instead.";

    public const string Failed = "LibreNMS didn't create a token. Try again, or paste an API token instead.";

    public const string CertificateDeclined = "The certificate wasn't trusted, so DashyNMS can't sign in to this server.";

    public const string CertificateNotTrusted = "This server's certificate isn't trusted. Turn on \"Allow untrusted certificate\" to check and trust it, or use an API token instead.";

    public static string Unreachable(string host) => "Couldn't reach " + host + ". Check the address, and that this device can reach the server.";
}
