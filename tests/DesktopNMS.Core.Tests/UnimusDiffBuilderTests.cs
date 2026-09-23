using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusDiffBuilderTests
{
    private static UnimusDiffLineGroup Group(string type, (int n, string t)[] original, (int n, string t)[] revised) => new()
    {
        Type = type,
        OriginalLines = original.Select(l => new UnimusDiffLine { Number = l.n, Text = l.t }).ToList(),
        RevisedLines = revised.Select(l => new UnimusDiffLine { Number = l.n, Text = l.t }).ToList(),
    };

    private static (int, string)[] Lines(int from, int count, string prefix = "line") =>
        Enumerable.Range(from, count).Select(n => (n, $"{prefix} {n}")).ToArray();

    [Fact]
    public void Common_lines_carry_both_numbers_which_drift_after_a_deletion()
    {
        // Shape confirmed against a live Unimus diff: after a DELETED group,
        // the following COMMON group's revised numbers lag the original's.
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("DELETED", new[] { (1, "gone"), (2, "also gone") }, Array.Empty<(int, string)>()),
                Group("COMMON", new[] { (3, "kept") }, new[] { (1, "kept") }),
            },
        };

        var rows = UnimusDiffBuilder.Flatten(diff);

        Assert.Equal(3, rows.Count);
        Assert.Equal(UnimusDiffRowKind.Removed, rows[0].Kind);
        Assert.Null(rows[0].NewNumber);
        Assert.Equal(new UnimusDiffRow(UnimusDiffRowKind.Common, 3, 1, "kept"), rows[2]);
    }

    [Fact]
    public void Changed_groups_show_removed_then_added()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups = { Group("CHANGED", new[] { (5, "tagged A1-A2") }, new[] { (5, "tagged A1-A2,A4") }) },
        };

        var result = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions());

        Assert.Equal(UnimusDiffRowKind.Removed, result.Rows[0].Kind);
        Assert.Equal(UnimusDiffRowKind.Added, result.Rows[1].Kind);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(new[] { 0 }, result.ChangeStarts);
    }

    [Fact]
    public void Only_changed_collapses_distant_unchanged_lines_keeping_context()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("COMMON", Lines(1, 20), Lines(1, 20)),
                Group("CHANGED", new[] { (21, "old") }, new[] { (21, "new") }),
                Group("COMMON", Lines(22, 20), Lines(22, 20)),
            },
        };

        var result = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions { OnlyChanged = true, ContextLines = 3 });

        // hidden(17) + 3 context + removed + added + 3 context + hidden(17)
        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(UnimusDiffRowKind.Hidden, result.Rows[0].Kind);
        Assert.Equal(17, result.Rows[0].HiddenCount);
        Assert.Equal(18, result.Rows[1].OldNumber);
        Assert.Equal(UnimusDiffRowKind.Hidden, result.Rows[^1].Kind);
        Assert.Equal(17, result.Rows[^1].HiddenCount);
        Assert.Equal(new[] { 4 }, result.ChangeStarts);
    }

    [Fact]
    public void Expanding_a_hidden_run_shows_its_lines_again()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("COMMON", Lines(1, 20), Lines(1, 20)),
                Group("DELETED", new[] { (21, "gone") }, Array.Empty<(int, string)>()),
            },
        };

        var collapsed = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions { OnlyChanged = true });
        var hiddenStart = collapsed.Rows[0].HiddenStart;

        var expanded = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions
        {
            OnlyChanged = true,
            ExpandedHiddenStarts = new HashSet<int> { hiddenStart },
        });

        Assert.DoesNotContain(expanded.Rows, r => r.Kind == UnimusDiffRowKind.Hidden);
        Assert.Equal(21, expanded.Rows.Count);
    }

    [Fact]
    public void Ignore_empty_lines_drops_blank_only_changes_entirely()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("COMMON", new[] { (1, "hostname sw1") }, new[] { (1, "hostname sw1") }),
                Group("INSERTED", Array.Empty<(int, string)>(), new[] { (2, "   ") }),
            },
        };

        var result = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions { IgnoreEmptyLines = true });

        Assert.False(result.HasDifferences);
        Assert.Empty(result.ChangeStarts);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Separate_change_blocks_are_each_found()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("DELETED", new[] { (1, "a") }, Array.Empty<(int, string)>()),
                Group("COMMON", new[] { (2, "b") }, new[] { (1, "b") }),
                Group("INSERTED", Array.Empty<(int, string)>(), new[] { (2, "c"), (3, "d") }),
            },
        };

        var result = UnimusDiffBuilder.Build(diff, new UnimusDiffOptions());

        Assert.Equal(new[] { 0, 2 }, result.ChangeStarts);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
    }
}
