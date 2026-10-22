using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;

namespace DesktopNMS.Services;

// The platform services the Avalonia app doesn't have yet (#250), so the
// shared view models run unchanged: each does nothing, or says it isn't
// available, until its own issue lands.

/// <summary>Toast notifications - #251.</summary>
public sealed class NoAlertNotifications : IAlertNotificationService
{
    public event EventHandler<ToastActionRequest>? ActionRequested
    {
        add { }
        remove { }
    }

    public void Handle(AlertPollResult result)
    {
    }

    public void ShowPreview(AlertSeverity severity)
    {
    }

    public void ClearHistory()
    {
    }
}

/// <summary>Update checks and installs for each OS - #256.</summary>
public sealed class NoUpdates : IUpdateCheckService
{
    public string CurrentVersion { get; } = typeof(NoUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public ReadyUpdate? ReadyUpdate => null;

    public event EventHandler? ReadyUpdateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? InstallStarted
    {
        add { }
        remove { }
    }

    public Task<UpdateCheckResult> CheckAsync(bool notifyIfNewer, bool? includePreviewBuildsOverride = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new UpdateCheckResult(false, null, false));

    public Task<bool> InstallAsync() => Task.FromResult(false);

    public void OnStartup()
    {
    }
}

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

    public void Start()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Update());
        _timer.Start();
    }

    private void Update()
    {
        var onScreen = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                       && desktop.Windows.Any(w => w.IsVisible && w.WindowState != WindowState.Minimized);
        if (_isInBackground == !onScreen)
        {
            return;
        }

        _isInBackground = !onScreen;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
