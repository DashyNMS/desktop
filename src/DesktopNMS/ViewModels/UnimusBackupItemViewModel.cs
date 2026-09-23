using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// One line in a rendered config diff, flattened from
/// <see cref="UnimusBackupDiff"/>'s line groups into the shape a unified
/// diff view actually wants - see <see cref="FromDiff"/>.
/// </summary>
public sealed class UnimusDiffLineViewModel
{
    public UnimusDiffLineViewModel(string kind, int? lineNumber, string text)
    {
        Kind = kind;
        LineNumber = lineNumber;
        Text = text;
    }

    /// <summary>"common", "removed" or "added" - drives the row's colour in the view.</summary>
    public string Kind { get; }

    public int? LineNumber { get; }

    public string Text { get; }

    public bool IsRemoved => Kind == "removed";

    public bool IsAdded => Kind == "added";

    /// <summary>A leading "+ "/"- " so the diff doesn't rely on colour alone to read.</summary>
    public string Prefix => Kind switch
    {
        "removed" => "- ",
        "added" => "+ ",
        _ => "  ",
    };

    /// <summary>
    /// Flattens Unimus's line groups (COMMON/CHANGED/INSERTED/DELETED) into a
    /// single unified-diff-style sequence: a CHANGED group's original lines
    /// show as removed, followed by its revised lines as added - the same
    /// "old lines then new lines" shape a git-style unified diff uses for a
    /// modified block, since this app doesn't build a side-by-side view.
    /// </summary>
    public static IReadOnlyList<UnimusDiffLineViewModel> FromDiff(UnimusBackupDiff diff)
    {
        var lines = new List<UnimusDiffLineViewModel>();

        foreach (var group in diff.LineGroups)
        {
            if (group.IsCommon)
            {
                lines.AddRange(group.OriginalLines.Select(l => new UnimusDiffLineViewModel("common", l.Number, l.Text)));
            }
            else if (group.IsDeleted)
            {
                lines.AddRange(group.OriginalLines.Select(l => new UnimusDiffLineViewModel("removed", l.Number, l.Text)));
            }
            else if (group.IsInserted)
            {
                lines.AddRange(group.RevisedLines.Select(l => new UnimusDiffLineViewModel("added", l.Number, l.Text)));
            }
            else if (group.IsChanged)
            {
                lines.AddRange(group.OriginalLines.Select(l => new UnimusDiffLineViewModel("removed", l.Number, l.Text)));
                lines.AddRange(group.RevisedLines.Select(l => new UnimusDiffLineViewModel("added", l.Number, l.Text)));
            }
        }

        return lines;
    }
}
