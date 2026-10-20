using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DesktopNMS.Infrastructure;

/// <summary>RFC 4180 CSV serialization (issue #27) - shared by the Alerts tab and a device's event log export/copy commands.</summary>
public static class CsvWriter
{
    public static string ToCsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Escape)));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        return builder.ToString();
    }

    /// <summary>Leading characters Excel/Sheets/LibreOffice treat as the start of a formula - see <see cref="Escape"/>.</summary>
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// Wraps a field in quotes if it contains a comma, quote or newline,
    /// doubling up any embedded quotes. Fields here come from the LibreNMS
    /// server (alert notes, rule/device names, event log messages) -
    /// anything settable by anyone with write access to it, not just this
    /// app's own user - so a field starting with a formula-trigger character
    /// is prefixed with a single quote first (the standard CSV/formula-
    /// injection mitigation) so a spreadsheet app never executes it as a
    /// live formula on open.
    /// </summary>
    private static string Escape(string? field)
    {
        field ??= string.Empty;

        if (field.Length > 0 && field.IndexOfAny(FormulaTriggers) == 0)
        {
            field = "'" + field;
        }

        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
