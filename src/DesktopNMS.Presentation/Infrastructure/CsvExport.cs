using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Copy as CSV / Save as CSV for one table - the rows it's showing right
/// now, after search and filters (#67, #142). A table's view model owns one,
/// and its toolbar's CSV export button binds to it.
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

    private void Copy() => UiPlatform.Clipboard.SetText(ToCsv());

    private void Save()
    {
        var stem = string.Concat(_fileStem().Split(Path.GetInvalidFileNameChars()));
        var path = UiPlatform.FileDialogs.PickFileToSave(new FileDialogOptions("CSV files (*.csv)|*.csv", $"{stem}-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv"));
        if (path is not null)
        {
            File.WriteAllText(path, ToCsv());
        }
    }

    /// <summary>A row from values of any type - nulls become empty cells.</summary>
    public static IReadOnlyList<string> Row(params object?[] values) =>
        values.Select(v => v?.ToString() ?? string.Empty).ToArray();
}
