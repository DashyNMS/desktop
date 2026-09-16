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

    /// <summary>
    /// Ranks two release tags against each other so the best one can be
    /// picked out of a list mixing stable and preview builds (e.g. "1.0.0"
    /// vs "1.0.0-preview.2"). Compares the numeric core first; for the same
    /// core, a stable tag always outranks a preview tag, and between two
    /// previews the higher preview sequence number wins. Returns positive if
    /// <paramref name="a"/> ranks higher than <paramref name="b"/>, negative
    /// if lower, and zero if either tag cannot be parsed.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        var parsedA = Parse(a);
        var parsedB = Parse(b);

        if (parsedA is null || parsedB is null)
        {
            return 0;
        }

        var coreCompare = parsedA.Value.Core.CompareTo(parsedB.Value.Core);
        if (coreCompare != 0)
        {
            return coreCompare;
        }

        if (parsedA.Value.IsPreview != parsedB.Value.IsPreview)
        {
            return parsedA.Value.IsPreview ? -1 : 1;
        }

        return parsedA.Value.PreviewSequence.CompareTo(parsedB.Value.PreviewSequence);
    }

    // Matches a numeric core (1-4 dotted segments) with an optional
    // "-preview.N" (or "-preview1", "-preview") suffix - the tag shape
    // GitHubActions/the release process is expected to produce for a preview
    // build, e.g. "v1.0.0-preview.2".
    private static readonly Regex TagPattern = new(@"(?<core>\d+(\.\d+){1,3})(-preview\.?(?<seq>\d+)?)?", RegexOptions.IgnoreCase);

    private static (Version Core, bool IsPreview, int PreviewSequence)? Parse(string? tag)
    {
        if (tag is null)
        {
            return null;
        }

        var match = TagPattern.Match(tag);
        if (!match.Success || !Version.TryParse(match.Groups["core"].Value, out var core))
        {
            return null;
        }

        var isPreview = tag.Contains("-preview", StringComparison.OrdinalIgnoreCase);
        var sequence = isPreview && int.TryParse(match.Groups["seq"].Value, out var n) ? n : 0;

        return (core, isPreview, sequence);
    }
}
