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

    /// <summary>Wraps a field in quotes if it contains a comma, quote or newline, doubling up any embedded quotes.</summary>
    private static string Escape(string? field)
    {
        field ??= string.Empty;

        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
