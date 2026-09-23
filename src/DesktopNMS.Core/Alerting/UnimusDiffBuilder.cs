using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

public enum UnimusDiffRowKind
{
    Common,
    Removed,
    Added,

    /// <summary>A collapsed run of unchanged lines - <see cref="UnimusDiffRow.HiddenCount"/> of them, starting at <see cref="UnimusDiffRow.HiddenStart"/>.</summary>
    Hidden,
}

/// <summary>
/// One rendered row of a config diff. <see cref="OldNumber"/>/<see cref="NewNumber"/>
/// are the line's numbers in the original/revised backup respectively - an
/// unchanged line has both (and they drift apart once lines are added or
/// removed above it), a removed line only the old one, an added line only
/// the new one.
/// </summary>
public sealed record UnimusDiffRow(
    UnimusDiffRowKind Kind,
    int? OldNumber,
    int? NewNumber,
    string Text,
    int HiddenCount = 0,
    int HiddenStart = -1);

public sealed class UnimusDiffOptions
{
    /// <summary>Collapse unchanged lines more than <see cref="ContextLines"/> away from any change into a single "N lines hidden" row.</summary>
    public bool OnlyChanged { get; init; }

    /// <summary>Drop blank/whitespace-only lines entirely before anything else - so a change that only adds or removes blank lines disappears too.</summary>
    public bool IgnoreEmptyLines { get; init; }

    public int ContextLines { get; init; } = 3;

    /// <summary>
    /// Hidden runs the user has expanded, keyed by <see cref="UnimusDiffRow.HiddenStart"/>.
    /// Only stable for a given <see cref="IgnoreEmptyLines"/> setting - the
    /// caller should clear it when that changes.
    /// </summary>
    public IReadOnlySet<int> ExpandedHiddenStarts { get; init; } = new HashSet<int>();
}

public sealed class UnimusDiffResult
{
    public required IReadOnlyList<UnimusDiffRow> Rows { get; init; }

    /// <summary>Index into <see cref="Rows"/> of the first row of each change block (a run of consecutive removed/added rows), in order.</summary>
    public required IReadOnlyList<int> ChangeStarts { get; init; }

    public int AddedCount { get; init; }

    public int RemovedCount { get; init; }

    public bool HasDifferences => AddedCount + RemovedCount > 0;
}

/// <summary>
/// Turns Unimus's <c>backups/diff</c> line groups into a unified-diff row
/// list for display (issue #115 follow-up) - line numbers for both sides,
/// optional blank-line filtering and collapsing of unchanged stretches, and
/// the positions of each change block so the view can jump between them.
/// </summary>
public static class UnimusDiffBuilder
{
    /// <summary>
    /// Every line of the diff, in order, with no filtering. A CHANGED group
    /// shows its original lines as removed followed by its revised lines as
    /// added - the same shape a git-style unified diff uses for a modified
    /// block.
    /// </summary>
    public static List<UnimusDiffRow> Flatten(UnimusBackupDiff diff)
    {
        var rows = new List<UnimusDiffRow>();

        foreach (var group in diff.LineGroups)
        {
            if (group.IsCommon)
            {
                // Confirmed live: a COMMON group carries the same lines on
                // both sides, numbered independently - the numbers diverge
                // after anything above was added or removed.
                for (var i = 0; i < group.OriginalLines.Count; i++)
                {
                    var original = group.OriginalLines[i];
                    var revised = i < group.RevisedLines.Count ? group.RevisedLines[i] : null;
                    rows.Add(new UnimusDiffRow(UnimusDiffRowKind.Common, original.Number, revised?.Number, original.Text));
                }
            }
            else if (group.IsDeleted)
            {
                rows.AddRange(group.OriginalLines.Select(l => new UnimusDiffRow(UnimusDiffRowKind.Removed, l.Number, null, l.Text)));
            }
            else if (group.IsInserted)
            {
                rows.AddRange(group.RevisedLines.Select(l => new UnimusDiffRow(UnimusDiffRowKind.Added, null, l.Number, l.Text)));
            }
            else if (group.IsChanged)
            {
                rows.AddRange(group.OriginalLines.Select(l => new UnimusDiffRow(UnimusDiffRowKind.Removed, l.Number, null, l.Text)));
                rows.AddRange(group.RevisedLines.Select(l => new UnimusDiffRow(UnimusDiffRowKind.Added, null, l.Number, l.Text)));
            }
        }

        return rows;
    }

    public static UnimusDiffResult Build(UnimusBackupDiff diff, UnimusDiffOptions options)
    {
        var all = Flatten(diff);

        if (options.IgnoreEmptyLines)
        {
            all.RemoveAll(r => string.IsNullOrWhiteSpace(r.Text));
        }

        var added = all.Count(r => r.Kind == UnimusDiffRowKind.Added);
        var removed = all.Count(r => r.Kind == UnimusDiffRowKind.Removed);

        var rows = options.OnlyChanged ? Collapse(all, options) : all;

        return new UnimusDiffResult
        {
            Rows = rows,
            ChangeStarts = FindChangeStarts(rows),
            AddedCount = added,
            RemovedCount = removed,
        };
    }

    private static List<UnimusDiffRow> Collapse(List<UnimusDiffRow> all, UnimusDiffOptions options)
    {
        var keep = new bool[all.Count];
        var context = Math.Max(0, options.ContextLines);

        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Kind is UnimusDiffRowKind.Removed or UnimusDiffRowKind.Added)
            {
                var from = Math.Max(0, i - context);
                var to = Math.Min(all.Count - 1, i + context);
                for (var j = from; j <= to; j++)
                {
                    keep[j] = true;
                }
            }
        }

        var rows = new List<UnimusDiffRow>();
        var index = 0;

        while (index < all.Count)
        {
            if (keep[index])
            {
                rows.Add(all[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < all.Count && !keep[index])
            {
                index++;
            }

            if (options.ExpandedHiddenStarts.Contains(start))
            {
                rows.AddRange(all.Skip(start).Take(index - start));
            }
            else
            {
                rows.Add(new UnimusDiffRow(UnimusDiffRowKind.Hidden, null, null, string.Empty, index - start, start));
            }
        }

        return rows;
    }

    private static List<int> FindChangeStarts(IReadOnlyList<UnimusDiffRow> rows)
    {
        var starts = new List<int>();
        var inChange = false;

        for (var i = 0; i < rows.Count; i++)
        {
            var isChange = rows[i].Kind is UnimusDiffRowKind.Removed or UnimusDiffRowKind.Added;
            if (isChange && !inChange)
            {
                starts.Add(i);
            }

            inChange = isChange;
        }

        return starts;
    }
}
