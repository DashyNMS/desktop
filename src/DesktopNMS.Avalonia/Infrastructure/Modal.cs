using Avalonia.Controls;
using Avalonia.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Shows an Avalonia window modally and waits for it (#250). The shared
/// <see cref="Services.IWindowService"/> asks synchronously - "bool Confirm(...)",
/// as WPF's ShowDialog answers - so this runs a nested dispatcher loop until
/// the window closes, as WPF's own modal loop does, rather than making every
/// view model async.
/// </summary>
public static class Modal
{
    /// <summary>Shows <paramref name="window"/> over <paramref name="owner"/> (or on its own) and returns once it's closed.</summary>
    public static void Show(Window window, Window? owner)
    {
        using var closed = new CancellationTokenSource();
        window.Closed += (_, _) => closed.Cancel();

        if (owner is { IsVisible: true })
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        Dispatcher.UIThread.MainLoop(closed.Token);
    }

    /// <summary>Waits for <paramref name="task"/> - a file picker, say - while the UI keeps running.</summary>
    public static T Wait<T>(Task<T> task)
    {
        using var done = new CancellationTokenSource();
        task.ContinueWith(_ => done.Cancel(), TaskScheduler.Default);
        if (!task.IsCompleted)
        {
            Dispatcher.UIThread.MainLoop(done.Token);
        }

        return task.GetAwaiter().GetResult();
    }
}
