using System.Text;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Turning what the Unimus tab shows into something to save or paste - a
/// safe file name for an exported backup, a standard unified diff for an
/// exported comparison, and display text for Copy (issue #115 follow-up).
/// </summary>
public static class UnimusExport
{
    /// <summary>
    /// "sw-core-01_2026-09-18_1801.cfg" - the device name plus the
    /// backup's own time, so several exports of the same device sort by date
    /// and never collide. Anything Windows won't allow in a file name becomes
    /// "_"; an empty name falls back to "device".
    /// </summary>
    public static string FileNameFor(string? deviceName, DateTime? localTime, string extension)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        var stamp = localTime is { } t ? $"_{t:yyyy-MM-dd_HHmm}" : string.Empty;
        var ext = extension.StartsWith('.') ? extension : "." + extension;

        return safe + stamp + ext;
    }

    /// <summary>
    /// A standard unified diff (the <c>diff -u</c> / <c>git diff</c> format)
    /// of the whole comparison, for saving to a .diff file that patch tools,
    /// git and diff viewers all understand: <c>---</c>/<c>+++</c> file
    /// headers, then one hunk per group of nearby changes with a real
    /// <c>@@ -start,count +start,count @@</c> header and
    /// <paramref name="context"/> unchanged lines either side. Changes closer
    /// together than twice the context share a hunk, as GNU diff does.
    /// Always built from the full diff - never the viewer's display options
    /// (collapsing, ignoring blank lines), which would make it describe
    /// something other than the real change. Empty when there are no changes.
    /// </summary>
    /// <param name="originalHeader">What follows "--- ", conventionally a file name, a tab, then a timestamp - see <see cref="FileHeader"/>.</param>
    public static string ToUnifiedDiff(UnimusBackupDiff diff, string originalHeader, string revisedHeader, int context = 3)
    {
        var rows = UnimusDiffBuilder.Flatten(diff);

        // Line numbers counted here rather than taken from Unimus's own:
        // a patch's hunk headers must agree exactly with its lines, so
        // they're derived from the same rows being written out.
        var oldNumbers = new int[rows.Count];
        var newNumbers = new int[rows.Count];
        int oldLine = 0, newLine = 0;
        var changes = new List<int>();

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != UnimusDiffRowKind.Added)
            {
                oldLine++;
            }

            if (rows[i].Kind != UnimusDiffRowKind.Removed)
            {
                newLine++;
            }

            // The number of the last line at or before this row on each side
            // - for a row absent from one side, the line it comes after.
            oldNumbers[i] = oldLine;
            newNumbers[i] = newLine;

            if (rows[i].Kind is UnimusDiffRowKind.Removed or UnimusDiffRowKind.Added)
            {
                changes.Add(i);
            }
        }

        if (changes.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.Append("--- ").Append(originalHeader).Append('\n');
        text.Append("+++ ").Append(revisedHeader).Append('\n');

        foreach (var (start, end) in Hunks(changes, rows.Count, context))
        {
            var hunk = rows.Skip(start).Take(end - start + 1).ToList();
            var oldCount = hunk.Count(r => r.Kind != UnimusDiffRowKind.Added);
            var newCount = hunk.Count(r => r.Kind != UnimusDiffRowKind.Removed);

            text.Append("@@ -").Append(Range(start, oldCount, oldNumbers, isOld: true, rows))
                .Append(" +").Append(Range(start, newCount, newNumbers, isOld: false, rows))
                .Append(" @@\n");

            foreach (var row in hunk)
            {
                text.Append(row.Kind switch
                {
                    UnimusDiffRowKind.Removed => '-',
                    UnimusDiffRowKind.Added => '+',
                    _ => ' ',
                });
                text.Append(row.Text).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// "sw-core-01_2026-08-05_1801.cfg&lt;tab&gt;2026-08-05 18:01:00 +0100" -
    /// the conventional "file name, tab, timestamp" a unified diff's
    /// ---/+++ lines carry, so tools show which backup is which side.
    /// </summary>
    public static string FileHeader(string? deviceName, DateTime? utcTime)
    {
        if (utcTime is not { } utc)
        {
            return FileNameFor(deviceName, null, ".cfg");
        }

        var local = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime();
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var zone = $"{sign}{Math.Abs(offset.Hours):00}{Math.Abs(offset.Minutes):00}";

        return $"{FileNameFor(deviceName, local.DateTime, ".cfg")}\t{local:yyyy-MM-dd HH:mm:ss} {zone}";
    }

    /// <summary>Row ranges (inclusive) for each hunk: every change plus its context, with ranges that touch or overlap merged.</summary>
    private static List<(int Start, int End)> Hunks(List<int> changes, int rowCount, int context)
    {
        var hunks = new List<(int Start, int End)>();
        var start = Math.Max(0, changes[0] - context);
        var end = Math.Min(rowCount - 1, changes[0] + context);

        foreach (var change in changes.Skip(1))
        {
            var changeStart = Math.Max(0, change - context);
            if (changeStart <= end + 1)
            {
                end = Math.Min(rowCount - 1, change + context);
            }
            else
            {
                hunks.Add((start, end));
                start = changeStart;
                end = Math.Min(rowCount - 1, change + context);
            }
        }

        hunks.Add((start, end));
        return hunks;
    }

    /// <summary>
    /// "start,count" for one side of a hunk header, or just "start" when the
    /// count is 1 (as diff writes it). A side with no lines in the hunk (a
    /// pure insertion or deletion) gives the line it comes after - 0 at the
    /// very start of the file.
    /// </summary>
    private static string Range(int start, int count, int[] numbers, bool isOld, List<UnimusDiffRow> rows)
    {
        int first;
        if (count == 0)
        {
            first = start == 0 ? 0 : numbers[start - 1];
        }
        else
        {
            // The first row in the hunk that exists on this side.
            var skip = isOld ? UnimusDiffRowKind.Added : UnimusDiffRowKind.Removed;
            var index = start;
            while (rows[index].Kind == skip)
            {
                index++;
            }

            first = numbers[index];
        }

        return count == 1 ? $"{first}" : $"{first},{count}";
    }

    /// <summary>
    /// The rows as they're displayed, as diff-looking text for pasting
    /// (Copy): "-"/"+"/" " prefixed lines, a collapsed run as
    /// "@@ N unchanged lines @@". Follows the viewer's display options, so
    /// it's for reading, not a patch - see <see cref="ToUnifiedDiff"/> for that.
    /// </summary>
    public static string ToDiffText(IEnumerable<UnimusDiffRow> rows)
    {
        var text = new StringBuilder();

        foreach (var row in rows)
        {
            var line = row.Kind switch
            {
                UnimusDiffRowKind.Hidden => $"@@ {row.HiddenCount} unchanged {(row.HiddenCount == 1 ? "line" : "lines")} @@",
                UnimusDiffRowKind.Removed => "-" + row.Text,
                UnimusDiffRowKind.Added => "+" + row.Text,
                _ => " " + row.Text,
            };

            text.AppendLine(line);
        }

        return text.ToString();
    }
}
