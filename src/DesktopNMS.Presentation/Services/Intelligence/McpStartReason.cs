using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesktopNMS.Services.Intelligence;

/// <summary>
/// Why the network tools didn't start, as one fixed word (#325). The MCP
/// server explains a failed start in a line on stderr, but that line can hold
/// the server's address, a certificate or a setting's value, so it's matched
/// here in memory and only the word below is ever logged or acted on.
/// </summary>
internal static partial class McpStartReason
{
    public const string Certificate = "certificate";
    public const string Unauthorised = "unauthorised";
    public const string Unreachable = "unreachable";
    public const string Config = "config";
    public const string Unknown = "unknown";

    /// <summary>Lines kept from a start; the reason is in the last few.</summary>
    public const int MaxLines = 20;

    /// <summary>The reason the MCP server's stderr gives, from the wording in its Program.cs and LibreNmsStartup.cs.</summary>
    public static string Classify(IEnumerable<string> lines)
    {
        // Warnings (plain http://) are printed on every start, so they're no reason.
        var errors = lines.Where(line => !line.StartsWith("Warning:", StringComparison.Ordinal)).ToList();
        if (errors.Any(line => line.StartsWith("The certificate ", StringComparison.Ordinal)
                               && (line.Contains("isn't trusted", StringComparison.Ordinal) || line.Contains("isn't one listed in", StringComparison.Ordinal))))
        {
            return Certificate;
        }

        if (errors.Any(line => line.Contains("refused the API token", StringComparison.Ordinal)))
        {
            return Unauthorised;
        }

        if (errors.Any(line => line.StartsWith("Couldn't connect to LibreNMS", StringComparison.Ordinal)))
        {
            return Unreachable;
        }

        if (errors.Any(line => SettingProblem().IsMatch(line)))
        {
            return Config;
        }

        return Unknown;
    }

    /// <summary>What to tell the user for a reason; null keeps the general message.</summary>
    public static string? Describe(string reason) => reason switch
    {
        Certificate => "DashyNMS's network tools couldn't verify your LibreNMS server's certificate. If it was renewed, sign in to LibreNMS again to trust the new one.",
        Unauthorised => "LibreNMS refused the API token the network tools use. Sign in to LibreNMS again, then try again.",
        Unreachable => "DashyNMS's network tools couldn't reach LibreNMS. Check your connection, then try again.",
        Config => "DashyNMS's network tools didn't accept their settings. Check the LibreNMS, Unimus and Graylog settings, then try again.",
        _ => null,
    };

    // "LIBRENMS_URL is not set.", "GRAYLOG_VERSION: '3' isn't supported.", "Set both UNIMUS_URL and …".
    [GeneratedRegex(@"^(Set (both |)[A-Z]+_[A-Z_]+|(LIBRENMS|UNIMUS|GRAYLOG|MCP)_[A-Z_]+( is not set|:))", RegexOptions.CultureInvariant)]
    private static partial Regex SettingProblem();
}
