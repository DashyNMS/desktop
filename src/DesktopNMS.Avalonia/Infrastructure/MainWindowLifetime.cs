using Avalonia.Controls;
using Avalonia.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>Keep monitoring after closing the window; let Quit and OS shutdown finish.</summary>
public static class MainWindowLifetime
{
    public static void Attach(Window window, Func<bool> shouldHide)
        => Attach(window, shouldHide, OperatingSystem.IsMacOS(),
            completed => new MacFullScreenExit(window, completed));

    internal static void Attach(Window window, Func<bool> shouldHide, bool isMacOS,
        Func<Action, IDisposable> observeFullScreenExit)
    {
        IDisposable? pendingExit = null;
        var generation = 0;

        void CancelPending()
        {
            generation++;
            pendingExit?.Dispose();
            pendingExit = null;
        }

        window.Closed += (_, _) => CancelPending();
        window.Closing += (_, e) =>
        {
            // Avalonia's managed caption button calls Close(), so a user's
            // click can be programmatic. The reason distinguishes it from Quit.
            if (e.CloseReason is not (WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
                || !shouldHide())
            {
                CancelPending();
                return;
            }

            e.Cancel = true;
            if (pendingExit != null) return;
            if (!isMacOS || window.WindowState != WindowState.FullScreen)
            {
                window.Hide();
                return;
            }

            var request = ++generation;
            pendingExit = observeFullScreenExit(() =>
            {
                // Leave AppKit's notification/delegate stack before hiding or releasing
                // the observer. No timer: this runs only after native completion.
                Dispatcher.UIThread.Post(() =>
                {
                    if (request != generation) return;
                    CancelPending();
                    if (shouldHide()) window.Hide();
                });
            });
            window.WindowState = WindowState.Normal;
        };
    }
}
