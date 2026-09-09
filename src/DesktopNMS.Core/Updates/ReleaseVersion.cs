using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Updates;

/// <summary>Compares a GitHub release tag against the running app's version.</summary>
public static class ReleaseVersion
{
    /// <summary>
    /// True if <paramref name="candidateTag"/> (e.g. "v0.2.0", "0.2.0") is a
    /// higher version than <paramref name="currentVersion"/> (e.g. "0.1.0").
    /// Anything that cannot be parsed as a version is treated as not newer, so
    /// a malformed or unexpected tag never produces a false "update available".
    /// </summary>
    public static bool IsNewer(string? candidateTag, string? currentVersion)
    {
        if (candidateTag is null || currentVersion is null)
        {
            return false;
        }

        var candidateText = Regex.Match(candidateTag, @"\d+(\.\d+){1,3}").Value;

        return Version.TryParse(candidateText, out var candidate)
            && Version.TryParse(currentVersion, out var current)
            && candidate > current;
    }
}
