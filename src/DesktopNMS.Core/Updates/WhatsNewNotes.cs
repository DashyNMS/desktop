using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Core.Updates;

/// <summary>One change: the bold lead-in of its bullet, the rest of it, and an optional icon name.</summary>
public sealed record WhatsNewItem(string Title, string Text, string? Icon);

/// <summary>One "## New" / "## Improved" / "## Fixed" section.</summary>
public sealed record WhatsNewSection(string Name, IReadOnlyList<WhatsNewItem> Items);

/// <summary>
/// The "What's new" notes bundled with a release (#227) - WhatsNew.md, written
/// at release time in the release notes' own shape, so the dialog works
/// offline and shows exactly what shipped:
/// <code>
/// # 1.1.0
/// Released 18 October 2026
///
/// ## New
/// - **Maps.** Network, geographical and custom maps. {map}
/// </code>
/// Each bullet's bold lead-in is its title and the rest its description; a
/// trailing <c>{name}</c> picks its icon. Indented lines carry a bullet on.
/// </summary>
public sealed record WhatsNewNotes(string Version, string? Released, IReadOnlyList<WhatsNewSection> Sections)
{
    /// <summary>Every change, across the sections.</summary>
    public int Count => Sections.Sum(s => s.Items.Count);

    /// <summary>A one-line taste for Settings, e.g. "Maps, Neighbours, Graylog integration and 12 more changes".</summary>
    public string Summary(int names = 3)
    {
        var titles = Sections.SelectMany(s => s.Items).Take(names).Select(i => i.Title).ToList();
        var more = Count - titles.Count;
        return more switch
        {
            <= 0 when titles.Count == 1 => titles[0],
            <= 0 => string.Join(", ", titles.Take(titles.Count - 1)) + " and " + titles[^1],
            1 => string.Join(", ", titles) + " and 1 more change",
            _ => string.Join(", ", titles) + $" and {more} more changes",
        };
    }

    /// <summary>The notes, or null when there's no version heading or not one change in them.</summary>
    public static WhatsNewNotes? Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        string? version = null;
        string? released = null;
        var sections = new List<(string Name, List<string> Bullets)>();

        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add((trimmed[3..].Trim(), new List<string>()));
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                version ??= trimmed[2..].Trim().TrimStart('v', 'V');
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                if (sections.Count > 0)
                {
                    sections[^1].Bullets.Add(trimmed[2..].Trim());
                }
            }
            else if (trimmed.Length > 0 && line.Length > trimmed.Length && sections.Count > 0 && sections[^1].Bullets.Count > 0)
            {
                // An indented line carries the bullet above on.
                sections[^1].Bullets[^1] += " " + trimmed;
            }
            else if (trimmed.Length > 0 && sections.Count == 0 && version is not null)
            {
                released ??= trimmed;
            }
        }

        var parsed = sections
            .Select(s => new WhatsNewSection(s.Name, s.Bullets.Select(ParseItem).ToList()))
            .Where(s => s.Items.Count > 0)
            .ToList();

        return version is null || parsed.Count == 0 ? null : new WhatsNewNotes(version, released, parsed);
    }

    /// <summary>
    /// Whether to show these notes now: a release (never a preview, which has
    /// its own "updated" toast), these notes are for it, and they haven't been
    /// shown for it yet.
    /// </summary>
    public static bool IsDue(WhatsNewNotes? notes, string currentVersion, string? shownVersion)
        => notes is not null
           && !currentVersion.Contains('-', StringComparison.Ordinal)
           && SameVersion(notes.Version, currentVersion)
           && !SameVersion(shownVersion, currentVersion);

    private static bool SameVersion(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(a.Trim().TrimStart('v', 'V'), b.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Called once at start-up: a fresh install - nothing to sign in with and
    /// never a "What's new" shown - has nothing that's new to it, so this
    /// version counts as seen. An upgrade (even from 1.0, which didn't record
    /// the version it ran) has a server saved, so it still gets them.
    /// </summary>
    public static bool MarkSeenIfFreshInstall(AppSettings settings, string currentVersion)
    {
        if (settings.WhatsNewShownVersion is not null || !string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            return false;
        }

        settings.WhatsNewShownVersion = currentVersion;
        return true;
    }

    private static WhatsNewItem ParseItem(string bullet)
    {
        string? icon = null;
        if (bullet.EndsWith('}') && bullet.LastIndexOf('{') is var open and >= 0)
        {
            icon = bullet[(open + 1)..^1].Trim();
            bullet = bullet[..open].TrimEnd();
            if (icon.Length == 0)
            {
                icon = null;
            }
        }

        if (bullet.StartsWith("**", StringComparison.Ordinal) && bullet.IndexOf("**", 2, StringComparison.Ordinal) is var close and > 2)
        {
            var title = bullet[2..close].Trim().TrimEnd('.', ':').Trim();
            var text = bullet[(close + 2)..].Trim().TrimStart('-', '–', '—', ':').Trim();
            return new WhatsNewItem(title, text, icon);
        }

        return new WhatsNewItem(bullet, string.Empty, icon);
    }
}
