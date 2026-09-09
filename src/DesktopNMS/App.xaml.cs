using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DesktopNMS;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\DashyNMS.SingleInstance";
    private const string ShowWindowEventName = @"Local\DashyNMS.ShowWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private CancellationTokenSource? _showWindowListener;
    private ServiceProvider? _services;
    private TrayIconService? _tray;
    private AlertMonitor? _monitor;
    private Views.MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private ILogger<App>? _logger;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Another copy already owns the tray icon. Bring its window up
            // rather than vanishing: launching the app and having nothing
            // happen is indistinguishable from a crash, and relaunching after
            // a rebuild is exactly when this happens.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        StartShowWindowListener();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AsyncRelayCommand.UnhandledError += OnCommandError;

        _services = BuildServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("DashyNMS starting");

        // Must happen before the first toast is sent, and costs nothing on the
        // runs where the shortcut already matches.
        ToastIdentity.EnsureStartMenuShortcut(_logger);

        var settings = _services.GetRequiredService<ISettingsStore>();
        settings.Load();

        SetUpTray();
        SetUpNotifications();

        _monitor = _services.GetRequiredService<AlertMonitor>();
        _mainViewModel = _services.GetRequiredService<MainViewModel>();

        _mainWindow = new Views.MainWindow(_mainViewModel, settings);
        base.MainWindow = _mainWindow;
        _services.GetRequiredService<WindowService>().AttachMainWindow(_mainWindow);

        var startHidden = settings.Current.StartMinimised
                          || e.Args.Any(arg => arg.Equals("--minimised", StringComparison.OrdinalIgnoreCase)
                                               || arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                          || ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

        if (!startHidden)
        {
            _mainWindow.Show();
        }

        // Restoring the session touches the network, so it must not block the
        // window from appearing.
        _ = Dispatcher.InvokeAsync(RestoreSessionAsync, DispatcherPriority.Background);
    }

    private async Task RestoreSessionAsync()
    {
        if (_services is null || _mainViewModel is null)
        {
            return;
        }

        var session = _services.GetRequiredService<ISessionService>();
        var windows = _services.GetRequiredService<IWindowService>();

        var restored = await session.TryRestoreAsync().ConfigureAwait(true);

        if (restored is null || !restored.Succeeded)
        {
            if (restored is { Succeeded: false })
            {
                _logger?.LogInformation("Saved session unusable: {Error}", restored.ErrorMessage);
            }

            windows.ShowMain();

            // If the user dismisses sign-in the app still runs; the monitor sits
            // idle until a session exists, and the tray offers a way back in.
            windows.ShowSignInDialog();
        }

        _monitor?.Start();
        _mainViewModel.OnConnected();
        UpdateTrayStatus();

        // Independent of the LibreNMS connection, so it still runs when
        // sign-in is dismissed. Fire-and-forget: a failed or slow GitHub
        // request must never delay or affect anything else at startup.
        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var updates = _services.GetRequiredService<IUpdateCheckService>();
            await updates.CheckAsync(notifyIfNewer: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Startup update check failed");
        }
    }

    // ------------------------------------------------------------------- DI

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddProvider(new FileLoggerProvider(LogLevel.Information));
        });

        services.AddDesktopNmsCore();

        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IDeviceCache, DeviceCache>();
        services.AddSingleton<IAlertRuleCache, AlertRuleCache>();
        services.AddSingleton<AlertMonitor>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIconService>());
        services.AddSingleton<AlertNotificationService>();
        services.AddSingleton<IAlertNotificationService>(sp => sp.GetRequiredService<AlertNotificationService>());
        services.AddSingleton<IStartupRegistration, RunKeyStartupRegistration>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowService>(sp => sp.GetRequiredService<WindowService>());
        services.AddSingleton<ISelfActionTracker, SelfActionTracker>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DeviceListViewModel>();
        services.AddSingleton<HealthViewModel>();
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    // ----------------------------------------------------------------- tray

    private void SetUpTray()
    {
        if (_services is null)
        {
            return;
        }

        _tray = _services.GetRequiredService<TrayIconService>();
        _tray.Initialise();

        _tray.OpenRequested += (_, _) => _services.GetRequiredService<IWindowService>().ShowMain();
        _tray.RefreshRequested += (_, _) => _mainViewModel?.RequestRefresh();
        _tray.DevicesRequested += (_, _) => _services.GetRequiredService<IWindowService>().ShowDevicesTab();
        _tray.SettingsRequested += (_, _) =>
        {
            _services.GetRequiredService<IWindowService>().ShowMain();
            _mainViewModel?.SettingsCommand.Execute(null);
        };
        _tray.SignOutRequested += (_, _) =>
        {
            if (_mainViewModel?.SignOutCommand.CanExecute(null) != true)
            {
                return;
            }

            _services.GetRequiredService<IWindowService>().ShowMain();
            _mainViewModel.SignOutCommand.Execute(null);
        };
        _tray.ExitRequested += (_, _) => ShutdownApplication();
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null || _mainViewModel is null)
        {
            return;
        }

        _tray.UpdateStatus(
            _mainViewModel.IsConnected,
            _mainViewModel.CriticalCount,
            _mainViewModel.WarningCount,
            _mainViewModel.IsConnected ? _mainViewModel.ServerDescription : "Not connected");
    }

    // ---------------------------------------------------------- notifications

    private void SetUpNotifications()
    {
        if (_services is null)
        {
            return;
        }

        var notifications = _services.GetRequiredService<AlertNotificationService>();
        notifications.Initialise();
        notifications.ActionRequested += OnToastActionRequested;

        var monitor = _services.GetRequiredService<AlertMonitor>();

        // Polled fires on the monitor's background thread. The tray-balloon
        // fallback path touches a WinForms component, so hop to the UI thread
        // before doing either job.
        monitor.Polled += (_, result) => Dispatcher.InvokeAsync(() =>
        {
            notifications.Handle(result);
            UpdateTrayStatus();
        });
    }

    private void OnToastActionRequested(object? sender, ToastActionRequest request)
    {
        // Activations arrive on a background thread from the COM activator.
        Dispatcher.InvokeAsync(() => HandleToastAction(request));
    }

    private async void HandleToastAction(ToastActionRequest request)
    {
        if (_mainViewModel is null || _services is null)
        {
            return;
        }

        try
        {
            switch (request.Action)
            {
                case ToastAction.Acknowledge when request.AlertId is { } ackId:
                    await _mainViewModel.AcknowledgeAsync(ackId).ConfigureAwait(true);
                    break;

                default:
                    _services.GetRequiredService<IWindowService>().ShowMain();
                    if (request.AlertId is { } showId)
                    {
                        _mainViewModel.SelectAlert(showId);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not act on a toast activation");
        }
    }

    // -------------------------------------------------------------- shutdown

    /// <summary>Tears everything down, including the tray icon, and exits.</summary>
    public void ShutdownApplication()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _logger?.LogInformation("DashyNMS shutting down");

        try
        {
            _monitor?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "The alert monitor did not stop cleanly");
        }

        _mainWindow?.CloseForExit();
        _tray?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _monitor?.Dispose();
        _tray?.Dispose();
        _services?.Dispose();

        // Deliberately not calling ToastNotificationManagerCompat.Uninstall():
        // that removes the Start menu shortcut and COM registration the toast
        // system needs, and is only appropriate when the app is uninstalled.

        _showWindowListener?.Cancel();
        _showWindowListener?.Dispose();
        _showWindowSignal?.Dispose();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    // ------------------------------------------------------------ resilience

    private bool ClaimSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);

        if (createdNew)
        {
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    /// <summary>
    /// Tells the copy that is already running to show itself. Best effort: if
    /// the signal cannot be delivered, exiting quietly is still the right
    /// outcome for this process.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception)
        {
            // Nothing useful to do, and no UI of our own to report it in.
        }
    }

    /// <summary>Watches for a second launch and surfaces the window when one happens.</summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowListener = new CancellationTokenSource();

            var signal = _showWindowSignal;
            var token = _showWindowListener.Token;

            Task.Run(
                () =>
                {
                    var waits = new WaitHandle[] { signal, token.WaitHandle };

                    while (!token.IsCancellationRequested)
                    {
                        if (WaitHandle.WaitAny(waits) != 0)
                        {
                            return;
                        }

                        Dispatcher.InvokeAsync(() => _services?.GetService<IWindowService>()?.ShowMain());
                    }
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Losing this only costs the second-launch convenience.
            _logger?.LogWarning(ex, "Could not start the second-instance listener");
        }
    }

    private void OnCommandError(object? sender, Exception ex)
    {
        _logger?.LogError(ex, "A command failed");
        _services?.GetService<IWindowService>()?.ShowError("Something went wrong", ex.Message);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread");

        MessageBox.Show(
            e.Exception.Message + Environment.NewLine + Environment.NewLine +
            "The problem has been written to the log in %APPDATA%\\DashyNMS\\logs.",
            "DashyNMS",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: a failed refresh or a broken binding should not take the
        // tray icon down with it.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread");
}
