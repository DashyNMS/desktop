using System.Text;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Turning what the Unimus tab shows into something to save or paste - a
/// safe file name for an exported backup, and a unified-diff-style text for
/// a comparison (issue #115 follow-up).
/// </summary>
public static class UnimusExport
{
    /// <summary>
    /// "r-sw-core-01_2026-09-18_1801.cfg" - the device name plus the
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
    /// The rows as unified-diff-style text: "-"/"+"/" " prefixed lines, a
    /// collapsed run as "@@ N unchanged lines @@". With labels, starts with
    /// the usual "--- original" / "+++ revised" header lines, so the file reads
    /// as a diff in any editor that highlights them.
    /// </summary>
    public static string ToDiffText(IEnumerable<UnimusDiffRow> rows, string? originalLabel = null, string? revisedLabel = null)
    {
        var text = new StringBuilder();

        if (originalLabel is not null && revisedLabel is not null)
        {
            text.Append("--- ").AppendLine(originalLabel);
            text.Append("+++ ").AppendLine(revisedLabel);
        }

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
