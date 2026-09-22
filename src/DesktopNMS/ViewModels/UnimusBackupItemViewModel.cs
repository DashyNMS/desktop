using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One row in a device's Config tab (issue #115) - a single Unimus backup
/// revision. Selecting exactly two rows (<see cref="IsSelectedForDiff"/>)
/// enables the diff action on <see cref="DeviceDetailViewModel"/>.
/// </summary>
public sealed class UnimusBackupItemViewModel : ObservableObject
{
    private bool _isSelectedForDiff;

    public UnimusBackupItemViewModel(UnimusBackup backup, Action<UnimusBackupItemViewModel> onSelectionChanged, Action<UnimusBackupItemViewModel> onView)
    {
        Backup = backup;
        ViewCommand = new RelayCommand(() => onView(this));
        _onSelectionChanged = onSelectionChanged;
    }

    private readonly Action<UnimusBackupItemViewModel> _onSelectionChanged;

    public UnimusBackup Backup { get; }

    public int Id => Backup.Id;

    public string DateText => Backup.ValidSinceUtc is { } t
        ? t.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)
        : "-";

    /// <summary>Whether this revision is still the current one, or was superseded by a later backup.</summary>
    public bool IsCurrent => Backup.ValidUntilUtc is null;

    public string StatusText => IsCurrent ? "Current" : "Superseded";

    public bool IsText => Backup.IsText;

    public bool IsSelectedForDiff
    {
        get => _isSelectedForDiff;
        set
        {
            if (SetProperty(ref _isSelectedForDiff, value))
            {
                _onSelectionChanged(this);
            }
        }
    }

    public RelayCommand ViewCommand { get; }
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
