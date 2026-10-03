using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Security;
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
    private bool _signInOpen;

    /// <summary>Passed to the fresh copy after Sign out: wipe everything saved before loading anything (#231).</summary>
    private const string ResetArgument = "--signed-out-reset";
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

        // Straight after Sign out (#231): forget everything saved on this
        // computer before any of it is loaded, so this start is the same as a
        // fresh install. Here rather than in the old copy - but only once the
        // old copy has gone, as it saves settings on its way out.
        IReadOnlyList<string>? resetFailures = null;
        var reset = Array.FindIndex(e.Args, a => a.Equals(ResetArgument, StringComparison.OrdinalIgnoreCase));
        if (reset >= 0)
        {
            if (reset + 1 < e.Args.Length && int.TryParse(e.Args[reset + 1], out var oldProcessId))
            {
                WaitForExit(oldProcessId);
            }

            resetFailures = LocalDataReset.Wipe(AppPaths.DataDirectory, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashyNMS"));
        }

        _services = BuildServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("DashyNMS starting");

        if (resetFailures is not null)
        {
            _logger.LogInformation("Signed out: everything saved on this computer was removed{Failures}",
                resetFailures.Count == 0 ? string.Empty : " except " + resetFailures.Count + " item(s) that were in use");

            // "Start with Windows" is a setting too.
            _services.GetRequiredService<IStartupRegistration>().SetEnabled(false);
        }

        // Must happen before the first toast is sent, and costs nothing on the
        // runs where the shortcut already matches.
        ToastIdentity.EnsureStartMenuShortcut(_logger);

        var settings = _services.GetRequiredService<ISettingsStore>();
        settings.Load();

        // Must run before any window (or anything else that applies a style)
        // is constructed - see ApplyTheme's remarks.
        // The brand fonts (#216), as every element's default - before any window opens.
        BrandFonts.ApplyAsDefault();
        ApplyTheme(settings.Current.Theme);
        WindowTheming.Register();

        // Before the main window exists, so the Logs tab (only shown when
        // Graylog is set up) is right from the first frame. Graylog doesn't
        // depend on the LibreNMS session, so there's no need to wait for it.
        ConfigureGraylogIfEnabled();

        AccentTheme.Apply(settings.Current.AccentColor);
        settings.Changed += (_, s) => AccentTheme.Apply(s.AccentColor);

        SetUpTray();
        SetUpNotifications();
        SetUpUpdates();

        // Slows the device and sensor pollers while nothing is on screen (#52).
        _services.GetRequiredService<AppActivity>().Start(Dispatcher);

        _monitor = _services.GetRequiredService<AlertMonitor>();
        _mainViewModel = _services.GetRequiredService<MainViewModel>();

        _mainWindow = new Views.MainWindow(_mainViewModel, settings);
        base.MainWindow = _mainWindow;
        _services.GetRequiredService<WindowService>().AttachMainWindow(_mainWindow);

        var startHidden = settings.Current.StartMinimised
                          || e.Args.Any(arg => arg.Equals("--minimised", StringComparison.OrdinalIgnoreCase)
                                               || arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                          || ToastNotificationManagerCompat.WasCurrentProcessToastActivated()

                          // Nothing to sign back in with (a first run, or just
                          // signed out): the sign-in window alone follows (#231).
                          || string.IsNullOrWhiteSpace(settings.Current.ServerUrl)
                          || !_services.GetRequiredService<ITokenProtector>().HasStoredToken;

        if (!startHidden)
        {
            _mainWindow.Show();
        }

        _mainViewModel.SignedOut += (_, _) => RestartSignedOut();

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

        // The beating mark over the main window until this settles (#228).
        _mainViewModel.IsSigningIn = true;
        ConnectionTestResult? restored;
        try
        {
            restored = await session.TryRestoreAsync().ConfigureAwait(true);

            // The saved session's certificate isn't trusted yet - typically the
            // first run after "Allow untrusted certificate" stopped meaning "accept
            // anything" (#189), or the certificate changed. Ask here, rather than
            // dropping to sign-in and asking for the token again.
            if (restored?.UntrustedCertificate is { } certificate)
            {
                windows.ShowMain();
                if (windows.ConfirmTrustCertificate("LibreNMS", certificate))
                {
                    session.TrustCertificate(certificate);
                    restored = await session.TryRestoreAsync().ConfigureAwait(true);
                }
            }
        }
        finally
        {
            _mainViewModel.IsSigningIn = false;
        }

        if (restored is null || !restored.Succeeded)
        {
            if (restored is { Succeeded: false })
            {
                _logger?.LogInformation("Saved session unusable: {Error}", restored.ErrorMessage);
            }

            // Just the sign-in window - no main window with nothing in it
            // behind (#231). If it's dismissed the app still runs in the tray;
            // the monitor sits idle until a session exists, and the tray
            // offers a way back in.
            ShowSignInOnly();
        }

        _monitor?.Start();
        _mainViewModel.OnConnected();
        UpdateTrayStatus();

        // Independent of the LibreNMS connection, so it still runs when
        // sign-in is dismissed. Fire-and-forget: a failed or slow GitHub
        // request must never delay or affect anything else at startup.
        _ = CheckForUpdatesAsync();

        // Also independent of the LibreNMS connection - Unimus has its own
        // enable/disable toggle and credentials (see UnimusSettings), set up
        // in Settings rather than tied to LibreNMS sign-in/out.
        ConfigureUnimusIfEnabled();
    }

    /// <summary>The same shape as <see cref="ConfigureUnimusIfEnabled"/> - Graylog is independent of LibreNMS sign-in too.</summary>
    private void ConfigureGraylogIfEnabled()
    {
        if (_services is null)
        {
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>().Current.Graylog;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Server))
        {
            return;
        }

        var password = _services.GetRequiredService<IGraylogPasswordProtector>().Load();
        var connection = GraylogConnection.FromSettings(settings, password, out var error);
        if (connection is null)
        {
            _logger?.LogWarning("Graylog is enabled but can't be connected to: {Error}", error);
            return;
        }

        _services.GetRequiredService<IGraylogApi>().Configure(connection);
    }

    private void ConfigureUnimusIfEnabled()
    {
        if (_services is null)
        {
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>().Current.Unimus;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url))
        {
            return;
        }

        var token = _services.GetRequiredService<IUnimusTokenProtector>().Load();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger?.LogWarning("Unimus is enabled but no API token is stored; the Config tab will show as unconfigured");
            return;
        }

        if (!UnimusConnection.TryParseWebRoot(settings.Url, out var webRoot, out var error) || webRoot is null)
        {
            _logger?.LogWarning("Unimus's configured URL is invalid: {Error}", error);
            return;
        }

        var connection = new UnimusConnection(webRoot, token, settings.AllowUntrustedCertificate, trustedCertificates: settings.TrustedCertificates);
        _services.GetRequiredService<IUnimusApi>().Configure(connection);
    }

    private void SetUpUpdates()
    {
        if (_services is null)
        {
            return;
        }

        var updates = _services.GetRequiredService<IUpdateCheckService>();

        // The installer replaces DashyNMS.exe, so get out of its way; it
        // reopens the app on the new version once it's done.
        updates.InstallStarted += (_, _) => Dispatcher.InvokeAsync(ShutdownApplication);
        updates.OnStartup();

        // Beyond the check at start-up, for an app left running for days in
        // the tray. Each release is only ever toasted about once.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        timer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        timer.Start();
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

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Merges the chosen palette, then Dark.xaml's styles, into
    /// Application.Resources - in that order, and both added here in code
    /// rather than declared in App.xaml.
    /// </summary>
    /// <remarks>
    /// Neither can be a static &lt;ResourceDictionary Source="..."/&gt; merge in
    /// App.xaml: which palette to use depends on a setting that is only
    /// known once <see cref="ISettingsStore"/> has loaded, which happens
    /// inside <see cref="OnStartup"/> - after App.xaml's own
    /// InitializeComponent has already run. If Dark.xaml were merged
    /// statically there, its styles would already be parsed - and every
    /// StaticResource reference inside them resolved, permanently, against
    /// whatever existed at that moment - before this method ever got a
    /// chance to add the right palette. Adding both here, in order, before
    /// any window exists, means Dark.xaml's styles are parsed for the first
    /// time only once the correct palette is already present.
    /// </remarks>
    private void ApplyTheme(AppTheme theme)
    {
        // "Match Windows" (#81) is resolved here, once - see ThemeState.
        ThemeState.Effective = theme.Resolve(ThemeState.WindowsUsesLightTheme());

        var paletteFile = ThemeState.Effective == AppTheme.Light ? "Palette.Light.xaml" : "Palette.Dark.xaml";

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Themes/{paletteFile}", UriKind.Relative),
        });

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Dark.xaml", UriKind.Relative),
        });
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

        // Secret storage is per platform - DPAPI here (#152).
        services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
        services.AddSingleton<IUnimusTokenProtector, DpapiUnimusTokenProtector>();
        services.AddSingleton<IGraylogPasswordProtector, DpapiGraylogPasswordProtector>();

        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IDeviceCache, DeviceCache>();
        services.AddSingleton<IAlertRuleCache, AlertRuleCache>();
        services.AddSingleton<IDeviceGroupMembershipService, DeviceGroupMembershipService>();
        services.AddSingleton<IMapLayoutStore, MapLayoutStore>();
        services.AddSingleton<IMapTileService, MapTileService>();
        services.AddSingleton<ICustomMapStore, CustomMapStore>();
        services.AddSingleton<IUnimusDeviceResolver, UnimusDeviceResolver>();
        services.AddSingleton<AppActivity>();
        services.AddSingleton<IAppActivity>(sp => sp.GetRequiredService<AppActivity>());
        services.AddSingleton<AlertMonitor>();
        services.AddSingleton<SensorMonitor>();
        services.AddSingleton<DeviceMonitor>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIconService>());
        services.AddSingleton<AlertNotificationService>();
        services.AddSingleton<IAlertNotificationService>(sp => sp.GetRequiredService<AlertNotificationService>());
        services.AddSingleton<IStartupRegistration, RunKeyStartupRegistration>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowService>(sp => sp.GetRequiredService<WindowService>());
        services.AddSingleton<ISelfActionTracker, SelfActionTracker>();
        services.AddSingleton<IDashboardLayoutService, DashboardLayoutService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DeviceListViewModel>();
        services.AddSingleton<HealthViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton<LocationsViewModel>();
        services.AddSingleton<IFleetLinks, FleetLinks>();
        services.AddSingleton<IFleetPorts, FleetPorts>();
        services.AddSingleton<INeighbourDirectory, NeighbourDirectory>();
        services.AddSingleton<NeighboursViewModel>();
        services.AddTransient<NeighbourViewEditorViewModel>();
        services.AddSingleton<RulesViewModel>();
        services.AddSingleton<TemplatesViewModel>();
        services.AddSingleton<NetworkMapViewModel>();
        services.AddSingleton<GeoMapViewModel>();
        services.AddSingleton<CustomMapsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AddDeviceViewModel>();
        services.AddTransient<BulkAddDevicesViewModel>();
        services.AddTransient<DeviceGroupEditorViewModel>();
        services.AddTransient<AddDevicesToGroupViewModel>();
        services.AddTransient<LocationEditorViewModel>();
        services.AddTransient<RuleEditorViewModel>();
        services.AddTransient<AlertTemplateEditorViewModel>();
        services.AddTransient<MaintenanceScheduleViewModel>();

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

        _tray.OpenRequested += (_, _) => ShowMainOrSignIn();
        _tray.RefreshRequested += (_, _) => _mainViewModel?.RequestRefresh();
        // Signed out, everything but Exit leads to the sign-in window (#231).
        _tray.DevicesRequested += (_, _) =>
        {
            if (_mainViewModel?.IsConnected == true)
            {
                _services.GetRequiredService<IWindowService>().ShowDevicesTab();
            }
            else
            {
                ShowMainOrSignIn();
            }
        };
        _tray.SettingsRequested += (_, _) =>
        {
            if (_mainViewModel?.IsConnected != true)
            {
                ShowMainOrSignIn();
                return;
            }

            _services.GetRequiredService<IWindowService>().ShowMain();
            _mainViewModel.SettingsCommand.Execute(null);
        };
        _tray.SignOutRequested += (_, _) =>
        {
            // The same item reads "Sign in…" while signed out.
            if (_mainViewModel?.SignOutCommand.CanExecute(null) != true)
            {
                ShowMainOrSignIn();
                return;
            }

            _services.GetRequiredService<IWindowService>().ShowMain();
            _mainViewModel.SignOutCommand.Execute(null);
        };
        _tray.ExitRequested += (_, _) => ShutdownApplication();
    }

    /// <summary>
    /// The sign-in window on its own, with the main window out of the way
    /// (#231). Signing in brings the main window up; dismissing it leaves
    /// the app in the tray. Returns whether it signed in.
    /// </summary>
    private bool ShowSignInOnly()
    {
        if (_services is null || _mainWindow is null || _signInOpen)
        {
            return false;
        }

        var windows = _services.GetRequiredService<IWindowService>();
        _signInOpen = true;
        try
        {
            _mainWindow.Hide();
            if (!windows.ShowSignInDialog())
            {
                return false;
            }
        }
        finally
        {
            _signInOpen = false;
        }

        windows.ShowMain();
        return true;
    }

    /// <summary>The tray's Open, and a second launch: the main window when signed in, otherwise the sign-in window (#231).</summary>
    private void ShowMainOrSignIn()
    {
        if (_services is null || _mainViewModel is null)
        {
            return;
        }

        if (_mainViewModel.IsConnected)
        {
            _services.GetRequiredService<IWindowService>().ShowMain();
        }
        else if (!_mainViewModel.IsSigningIn && ShowSignInOnly())
        {
            _monitor?.Start();
            _mainViewModel.OnConnected();
            UpdateTrayStatus();
        }
    }

    /// <summary>Waits (up to 20 s) for the copy that just signed out to finish closing, so its last save can't undo the reset.</summary>
    private static void WaitForExit(int processId)
    {
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(processId);
            old.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// After Sign out: start a fresh copy and close this one, so nothing
    /// from the server - tabs, caches, monitors, Device Details windows -
    /// can still be browsed (#231). The fresh copy has no token, so it opens
    /// on the sign-in window alone.
    /// </summary>
    private void RestartSignedOut()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            // Can't relaunch: at least don't leave anything on screen.
            _mainWindow?.Hide();
            UpdateTrayStatus();
            ShowMainOrSignIn();
            return;
        }

        _logger?.LogInformation("Signed out: restarting to the sign-in window");

        // Let go of the single-instance claim first, or the new copy would
        // find this one still running and just ask it to show itself.
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false };
            start.ArgumentList.Add(ResetArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not restart after signing out");
        }

        ShutdownApplication();
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

                case ToastAction.InstallUpdate:
                    await _mainViewModel.InstallUpdateAsync().ConfigureAwait(true);
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

                        Dispatcher.InvokeAsync(ShowMainOrSignIn);
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
