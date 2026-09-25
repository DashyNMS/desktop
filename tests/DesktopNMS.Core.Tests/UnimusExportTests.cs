using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusExportTests
{
    [Fact]
    public void File_name_combines_device_and_backup_time()
    {
        var name = UnimusExport.FileNameFor("sw-core-01", new DateTime(2026, 9, 18, 18, 1, 0), "cfg");

        Assert.Equal("sw-core-01_2026-09-18_1801.cfg", name);
    }

    [Fact]
    public void File_name_replaces_characters_windows_does_not_allow()
    {
        var name = UnimusExport.FileNameFor("core/sw:01*?", null, ".cfg");

        Assert.Equal("core_sw_01__.cfg", name);
    }

    [Fact]
    public void File_name_falls_back_when_the_device_has_no_name()
    {
        Assert.Equal("device.diff", UnimusExport.FileNameFor("  ", null, ".diff"));
    }

    [Fact]
    public void Display_text_has_prefixes_and_hidden_markers()
    {
        var rows = new[]
        {
            new UnimusDiffRow(UnimusDiffRowKind.Hidden, null, null, string.Empty, HiddenCount: 12, HiddenStart: 0),
            new UnimusDiffRow(UnimusDiffRowKind.Common, 13, 13, "vlan 10"),
            new UnimusDiffRow(UnimusDiffRowKind.Removed, 14, null, "   tagged A1"),
            new UnimusDiffRow(UnimusDiffRowKind.Added, null, 14, "   tagged A1,A4"),
        };

        var lines = UnimusExport.ToDiffText(rows).Split(Environment.NewLine);

        Assert.Equal("@@ 12 unchanged lines @@", lines[0]);
        Assert.Equal(" vlan 10", lines[1]);
        Assert.Equal("-   tagged A1", lines[2]);
        Assert.Equal("+   tagged A1,A4", lines[3]);
    }

    private static UnimusDiffLineGroup Group(string type, IEnumerable<(int n, string t)> original, IEnumerable<(int n, string t)> revised) => new()
    {
        Type = type,
        OriginalLines = original.Select(l => new UnimusDiffLine { Number = l.n, Text = l.t }).ToList(),
        RevisedLines = revised.Select(l => new UnimusDiffLine { Number = l.n, Text = l.t }).ToList(),
    };

    private static IEnumerable<(int, string)> Lines(int from, int to, int numberedFrom) =>
        Enumerable.Range(from, to - from + 1).Select((n, i) => (numberedFrom + i, $"line {n}"));

    private static readonly (int, string)[] None = Array.Empty<(int, string)>();

    [Fact]
    public void Unified_diff_matches_gnu_diff_output_exactly()
    {
        // 30 lines; line 2 changed, lines 12-13 deleted, two lines inserted
        // after line 26. Expected output is `diff -u orig.cfg rev.cfg` from
        // GNU diffutils on exactly these files - three separate hunks, since
        // each gap between changes is more than twice the context.
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("COMMON", Lines(1, 1, 1), Lines(1, 1, 1)),
                Group("CHANGED", new[] { (2, "line 2") }, new[] { (2, "line 2 changed") }),
                Group("COMMON", Lines(3, 11, 3), Lines(3, 11, 3)),
                Group("DELETED", Lines(12, 13, 12), None),
                Group("COMMON", Lines(14, 26, 14), Lines(14, 26, 12)),
                Group("INSERTED", None, new[] { (25, "new A"), (26, "new B") }),
                Group("COMMON", Lines(27, 30, 27), Lines(27, 30, 27)),
            },
        };

        var text = UnimusExport.ToUnifiedDiff(diff, "orig.cfg", "rev.cfg");

        const string expected =
            "--- orig.cfg\n" +
            "+++ rev.cfg\n" +
            "@@ -1,5 +1,5 @@\n" +
            " line 1\n" +
            "-line 2\n" +
            "+line 2 changed\n" +
            " line 3\n" +
            " line 4\n" +
            " line 5\n" +
            "@@ -9,8 +9,6 @@\n" +
            " line 9\n" +
            " line 10\n" +
            " line 11\n" +
            "-line 12\n" +
            "-line 13\n" +
            " line 14\n" +
            " line 15\n" +
            " line 16\n" +
            "@@ -24,6 +22,8 @@\n" +
            " line 24\n" +
            " line 25\n" +
            " line 26\n" +
            "+new A\n" +
            "+new B\n" +
            " line 27\n" +
            " line 28\n" +
            " line 29\n";

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Unified_diff_merges_changes_within_twice_the_context()
    {
        // Changes at lines 2 and 9: a gap of exactly 6 unchanged lines,
        // which GNU diff (context 3) joins into one hunk.
        var diff = new UnimusBackupDiff
        {
            LineGroups =
            {
                Group("COMMON", Lines(1, 1, 1), Lines(1, 1, 1)),
                Group("CHANGED", new[] { (2, "line 2") }, new[] { (2, "two") }),
                Group("COMMON", Lines(3, 8, 3), Lines(3, 8, 3)),
                Group("CHANGED", new[] { (9, "line 9") }, new[] { (9, "nine") }),
                Group("COMMON", Lines(10, 20, 10), Lines(10, 20, 10)),
            },
        };

        var text = UnimusExport.ToUnifiedDiff(diff, "a", "b");

        Assert.Single(text.Split('\n'), l => l.StartsWith("@@"));
        Assert.Contains("@@ -1,12 +1,12 @@\n", text);
    }

    [Fact]
    public void Unified_diff_of_an_insertion_into_an_empty_file_starts_at_zero()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups = { Group("INSERTED", None, new[] { (1, "hostname sw1"), (2, "vlan 10") }) },
        };

        var text = UnimusExport.ToUnifiedDiff(diff, "a", "b");

        Assert.Contains("@@ -0,0 +1,2 @@\n", text);
    }

    [Fact]
    public void Unified_diff_writes_single_line_ranges_without_a_count()
    {
        var diff = new UnimusBackupDiff
        {
            LineGroups = { Group("CHANGED", new[] { (1, "old") }, new[] { (1, "new") }) },
        };

        Assert.Contains("@@ -1 +1 @@\n", UnimusExport.ToUnifiedDiff(diff, "a", "b"));
    }

    [Fact]
    public void Unified_diff_is_empty_when_nothing_changed()
    {
        var diff = new UnimusBackupDiff { LineGroups = { Group("COMMON", Lines(1, 5, 1), Lines(1, 5, 1)) } };

        Assert.Equal(string.Empty, UnimusExport.ToUnifiedDiff(diff, "a", "b"));
    }

    [Fact]
    public void Raw_bytes_decode_binary_backups_too()
    {
        var backup = new UnimusBackup { Type = "BINARY", Bytes = Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }) };

        Assert.Null(backup.Content);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, backup.RawBytes);
    }

    [Fact]
    public void Raw_bytes_are_null_for_bad_or_missing_content()
    {
        Assert.Null(new UnimusBackup { Bytes = "not base64!" }.RawBytes);
        Assert.Null(new UnimusBackup { Bytes = null }.RawBytes);
    }
}
