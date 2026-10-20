using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DesktopNMS.Services;

/// <summary>
/// Desktop's <see cref="IAppActivity"/>: in the background when no DashyNMS
/// window is on screen - it's sitting in the tray, or every window is
/// minimised. <see cref="IAppActivity.Changed"/> is raised on the UI thread.
/// </summary>
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
