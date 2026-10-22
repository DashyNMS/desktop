using Avalonia.Controls;

namespace DesktopNMS.Infrastructure;

/// <summary>Keep monitoring after closing the window; let Quit and OS shutdown finish.</summary>
public static class MainWindowLifetime
{
    public static void Attach(Window window, Func<bool> shouldHide)
    {
        window.Closing += (_, e) =>
        {
            // Avalonia's managed caption button calls Close(), so a user's
            // click can be programmatic. The reason distinguishes it from Quit.
            if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
                && shouldHide())
            {
                e.Cancel = true;
                window.Hide();
            }
        };
    }
}
