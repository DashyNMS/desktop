using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DesktopNMS.Core.Services;

namespace DesktopNMS.Services;

/// <summary>
/// "In the background" when no DashyNMS window is on screen - minimised or
/// hidden - so the pollers slow down (#52), as the WPF app's AppActivity does.
/// </summary>
public sealed class AvaloniaAppActivity : IAppActivity
{
    private DispatcherTimer? _timer;
    private volatile bool _isInBackground;

    public bool IsInBackground => _isInBackground;

    public event EventHandler? Changed;

    /// <summary>UI-thread-only visibility check, including changes since the periodic update.</summary>
    public bool IsForegroundOnUiThread => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && desktop.Windows.Any(w => w.IsVisible && w.WindowState != WindowState.Minimized);

    public void Start()
    {
        Update();
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Update());
        _timer.Start();
    }

    private void Update()
    {
        var onScreen = IsForegroundOnUiThread;
        if (_isInBackground == !onScreen)
        {
            return;
        }

        _isInBackground = !onScreen;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
