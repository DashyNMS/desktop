using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DesktopNMS.Services;

/// <summary>
/// Whether any DashyNMS window is on screen - none means it's sitting in the
/// tray, or every window is minimised. The device and sensor pollers slow
/// down while it is (#52); the alert poller never does, since notifications
/// depend on it.
/// </summary>
public interface IAppActivity
{
    /// <summary>No window is on screen. Safe to read from any thread.</summary>
    bool IsInBackground { get; }

    /// <summary>Raised on the UI thread when <see cref="IsInBackground"/> changes.</summary>
    event EventHandler? Changed;
}

public sealed class AppActivity : IAppActivity
{
    private volatile bool _isInBackground;
    private DispatcherTimer? _timer;

    public bool IsInBackground => _isInBackground;

    public event EventHandler? Changed;

    /// <summary>
    /// Starts watching, on the UI thread. A short timer rather than per-window
    /// events: windows come and go (Device Details, dialogs), and minimising
    /// or hiding one raises nothing a class handler can catch.
    /// </summary>
    public void Start(Dispatcher dispatcher)
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Update(), dispatcher);
        _timer.Start();
    }

    private void Update()
    {
        var onScreen = Application.Current?.Windows
            .OfType<Window>()
            .Any(w => w.IsVisible && w.WindowState != WindowState.Minimized) ?? false;

        if (_isInBackground == !onScreen)
        {
            return;
        }

        _isInBackground = !onScreen;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>How much the device and sensor pollers slow down with nothing on screen (#52).</summary>
public static class AppActivityPolling
{
    /// <summary>Poll this many times less often - a 30-second setting becomes 2 minutes.</summary>
    public const int BackgroundSlowdown = 4;
}
