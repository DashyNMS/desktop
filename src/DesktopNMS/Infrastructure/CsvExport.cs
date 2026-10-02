using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Copy as CSV / Save as CSV for one table - the rows it's showing right
/// now, after search and filters (#67, #142). A table's view model owns one,
/// and its toolbar's <see cref="Views.CsvExportButton"/> binds to it.
/// </summary>
public sealed class CsvExport
{
    private readonly Func<string> _fileStem;
    private readonly IReadOnlyList<string> _headers;
    private readonly Func<IEnumerable<IReadOnlyList<string>>> _rows;

    /// <param name="fileStem">The saved file's name before the timestamp - "rules", or "core-sw-01-ports".</param>
    /// <param name="headers">The column headings.</param>
    /// <param name="rows">The rows as shown, one string per column.</param>
    public CsvExport(Func<string> fileStem, IReadOnlyList<string> headers, Func<IEnumerable<IReadOnlyList<string>>> rows)
    {
        _fileStem = fileStem;
        _headers = headers;
        _rows = rows;

        CopyCommand = new RelayCommand(Copy);
        SaveCommand = new RelayCommand(Save);
    }

    public CsvExport(string fileStem, IReadOnlyList<string> headers, Func<IEnumerable<IReadOnlyList<string>>> rows)
        : this(() => fileStem, headers, rows)
    {
    }

    public RelayCommand CopyCommand { get; }

    public RelayCommand SaveCommand { get; }

    public string ToCsv() => CsvWriter.ToCsv(_headers, _rows());

    private void Copy()
    {
        try
        {
            Clipboard.SetText(ToCsv());
        }
        catch (ExternalException)
        {
            // Another process briefly holds the clipboard - not worth an error.
        }
    }

    private void Save()
    {
        var stem = string.Concat(_fileStem().Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"{stem}-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv",
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, ToCsv());
        }
    }

    /// <summary>A row from values of any type - nulls become empty cells.</summary>
    public static IReadOnlyList<string> Row(params object?[] values) =>
        values.Select(v => v?.ToString() ?? string.Empty).ToArray();
}
