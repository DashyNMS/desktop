using System.Text;

namespace DesktopNMS.Core.Devices;

/// <summary>
/// Reads RFC 4180 CSV: quoted fields (which may hold commas, quotes as "" and
/// line breaks), CRLF or LF line endings and an optional byte-order mark.
/// Excel in many European locales saves "CSV" with semicolons instead of
/// commas, so the delimiter is worked out from the header line.
/// </summary>
public static class CsvReader
{
    /// <summary>Every record, each with its 1-based starting line number. Blank lines are skipped.</summary>
    public static IReadOnlyList<CsvRecord> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var delimiter = DetectDelimiter(text);
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var fieldStarted = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();

            // A line with nothing on it at all isn't a record.
            if (!(fields.Count == 1 && fields[0].Length == 0))
            {
                records.Add(new CsvRecord(recordLine, fields.ToArray()));
            }

            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (field.Length > 0 || fields.Count > 0 || fieldStarted)
        {
            EndRecord();
        }

        return records;
    }

    /// <summary>Comma, unless the first line has more semicolons than commas outside quotes.</summary>
    private static char DetectDelimiter(string text)
    {
        var commas = 0;
        var semicolons = 0;
        var inQuotes = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == '\r' || c == '\n'))
            {
                break;
            }
            else if (!inQuotes && c == ',')
            {
                commas++;
            }
            else if (!inQuotes && c == ';')
            {
                semicolons++;
            }
        }

        return semicolons > commas ? ';' : ',';
    }
}

/// <summary>One CSV record and the line it starts on (for error messages).</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);
