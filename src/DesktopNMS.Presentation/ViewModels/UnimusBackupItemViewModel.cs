using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One row in a device's Unimus tab (issue #115) - a single backup revision.
/// Plain data, no selection state of its own: the grid's native row
/// selection drives everything (see DeviceView.xaml.cs's SelectionChanged
/// handler and <see cref="DeviceDetailViewModel.OnConfigSelectionChanged"/>)
/// rather than a per-row checkbox/command.
/// </summary>
public sealed class UnimusBackupItemViewModel
{
    /// <param name="isCurrent">
    /// Whether this is the newest backup for the device (highest
    /// <see cref="Models.UnimusBackup.ValidSince"/>) - the caller decides
    /// this from the list as a whole, not from anything on the backup
    /// itself. <see cref="Models.UnimusBackup.ValidUntilUtc"/> looked like
    /// the obvious signal (null = never superseded) but live data disproved
    /// that: Unimus's own <c>/backups/latest</c> for a device returned its
    /// current backup with <c>validUntil</c> already set, weeks in the
    /// future - it isn't a "superseded at" timestamp at all, so every row
    /// was showing "Superseded" before this was reworked to just compare
    /// dates.
    /// </param>
    public UnimusBackupItemViewModel(UnimusBackup backup, bool isCurrent)
    {
        Backup = backup;
        IsCurrent = isCurrent;
    }

    public UnimusBackup Backup { get; }

    public int Id => Backup.Id;

    public string DateText => Backup.ValidSinceUtc is { } t
        ? t.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)
        : "-";

    public bool IsCurrent { get; }

    public string StatusText => IsCurrent ? "Current" : "Previous";

    public bool IsText => Backup.IsText;
}


/// <summary>
/// One row in the Unimus tab's line viewer - a diff row (see
/// <see cref="UnimusDiffBuilder"/>), or a plain numbered line when a single
/// backup is being viewed rather than compared.
/// </summary>
public sealed class UnimusDiffLineViewModel
{
    public UnimusDiffLineViewModel(UnimusDiffRow row)
    {
        Row = row;
    }

    public UnimusDiffRow Row { get; }

    public string OldNumberText => Row.OldNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewNumberText => Row.NewNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string Text => IsHidden
        ? $"... {Row.HiddenCount} unchanged {(Row.HiddenCount == 1 ? "line" : "lines")} hidden - click to show"
        : Row.Text;

    public bool IsRemoved => Row.Kind == UnimusDiffRowKind.Removed;

    public bool IsAdded => Row.Kind == UnimusDiffRowKind.Added;

    public bool IsHidden => Row.Kind == UnimusDiffRowKind.Hidden;

    public bool IsChange => IsRemoved || IsAdded;

    /// <summary>A leading "+"/"-" so the diff doesn't rely on colour alone to read.</summary>
    public string Prefix => Row.Kind switch
    {
        UnimusDiffRowKind.Removed => "-",
        UnimusDiffRowKind.Added => "+",
        _ => string.Empty,
    };

    /// <summary>A single backup's content as numbered rows, for the same viewer the diff uses.</summary>
    public static IReadOnlyList<UnimusDiffLineViewModel> FromContent(string content) =>
        content.Replace("\r\n", "\n").Split('\n')
            .Select((text, i) => new UnimusDiffLineViewModel(new UnimusDiffRow(UnimusDiffRowKind.Common, i + 1, null, text)))
            .ToList();
}
