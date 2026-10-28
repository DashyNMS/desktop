using DesktopNMS.Core.Updates;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Services;

/// <summary>Queue once and recheck readiness after dispatch, so sign-out or hiding cannot open a stale announcement.</summary>
internal sealed class ReleaseNotesPresenter
{
    private bool _scheduled;

    public void TrySchedule(WhatsNewNotes? notes, string version, Func<string?> seenVersion,
        Func<bool> ready, Action<Action> dispatch, Action show)
    {
        bool IsDue() => ready() && BundledWhatsNew.IsDue(notes, version, seenVersion());
        if (_scheduled || !IsDue()) return;
        _scheduled = true;
        try
        {
            dispatch(() =>
            {
                try { if (IsDue()) show(); }
                finally { _scheduled = false; }
            });
        }
        catch { _scheduled = false; throw; }
    }
}
