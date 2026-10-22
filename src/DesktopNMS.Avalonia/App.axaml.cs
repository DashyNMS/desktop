using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DesktopNMS.Core;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Topology;
using DesktopNMS.Demo;
using DesktopNMS.Infrastructure;
using DesktopNMS.Security;
using DesktopNMS.Services;
using DesktopNMS.Services.Intelligence;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesktopNMS;

/// <summary>
/// The cross-platform app's start-up (#250), following the WPF app's
/// App.OnStartup: the data folder, the UI thread, services, theme, the main
/// window, then restoring the saved session or asking to sign in.
/// </summary>
public partial class App : Application
{
    /// <summary>Started again after Sign out, with the old copy's process id: wait for it, then wipe (#231).</summary>
    private const string FreshStartArgument = "--fresh-start";

    /// <summary>Into or out of demo mode: the copy that asked, by process id, to wait for.</summary>
    private const string AfterArgument = "--after";

    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private bool _restartFresh;
    private TrayIconService? _tray;
    private bool _trayUpdatePending;
    private DemoMode _demo = new(isActive: false);
    private DemoServer? _demoServer;
    private DemoFleet? _demoFleet;
    private bool _startDemoOnExit;
    private bool _leaveDemoOnExit;
    private bool _whatsNewScheduled;
    private IUiTimer? _updateTimer;
    private DesktopInstance? _instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];

        // Demo mode (DemoMode): the whole app on an example network, in its own
        // data folder emptied each time - the real settings are never touched.
        var isDemo = args.Any(a => a.Equals(DemoMode.Argument, StringComparison.OrdinalIgnoreCase));
        var shots = Array.FindIndex(args, a => a.Equals(DemoMode.ScreenshotsArgument, StringComparison.OrdinalIgnoreCase));
        _demo = new DemoMode(isDemo, isDemo && shots >= 0 && shots + 1 < args.Length ? Path.GetFullPath(args[shots + 1]) : null);
        var after = Array.FindIndex(args, a => a.Equals(AfterArgument, StringComparison.OrdinalIgnoreCase));
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var earlierProcessId))
        {
            WaitForExit(earlierProcessId);
        }

        var development = DesktopProfile.IsDevelopment(DesktopUpdateService.RunningVersion(),
#if DEBUG
            debug: true);
#else
            debug: false);
#endif
        var profile = _demo.IsActive ? DemoMode.DataDirectory
            : DesktopProfile.ForBuild(AppPaths.DefaultDataDirectory, OperatingSystem.IsWindows(), development);
        if (!_demo.IsRenderingScreenshots)
        {
            _instance = new DesktopInstance(profile, legacyWindowsIdentity: !development && !_demo.IsActive);
            if (!_instance.TryClaim(() => Dispatcher.UIThread.Post(ShowMainOrSignIn)))
            {
                _instance.SignalExisting(); _instance.Dispose(); _instance = null;
                desktop.Shutdown(); return;
            }
        }

        // A Windows release reuses the 1.x files and DPAPI purposes directly.
        // Development and non-Windows builds retain their existing profile.
        if (_demo.IsActive)
        {
            DemoMode.Reset();
        }

        AppPaths.UseDataDirectory(profile);

        // The fresh copy after Sign out (#231): once the closing copy has
        // really gone, everything it saved goes too.
        var fresh = Array.IndexOf(args, FreshStartArgument);
        if (fresh >= 0)
        {
            if (fresh + 1 < args.Length && int.TryParse(args[fresh + 1], out var oldProcessId))
            {
                WaitForExit(oldProcessId);
            }

            LocalDataReset.Wipe(AppPaths.DataDirectory, cacheDirectory: null);
        }

        // Before any view model: they capture the UI thread on construction.
        AvaloniaUiThread.Install();
        AvaloniaUiPlatform.Install();

        _services = BuildServices(_demo);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation(_demo.IsActive ? "DashyNMS (cross-platform preview) starting in demo mode on {OS}" : "DashyNMS (cross-platform preview) starting on {OS}", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        AsyncRelayCommand.UnhandledError += (_, ex) => _logger.LogError(ex, "A command failed");

        var settings = _services.GetRequiredService<ISettingsStore>();
        settings.Load();
        if (DesktopProfile.RestoreLegacyWindow(settings.Current)) settings.SaveQuietly();

        if (_demo.IsActive)
        {
            _demoFleet = new DemoFleet();
            _demoServer = new DemoServer(_demoFleet, _logger);
            _demoServer.Start();
            DemoMode.Seed(settings, _services.GetRequiredService<ITokenProtector>(), _demoServer.Address, _demoFleet, _demo.IsRenderingScreenshots);
        }

        // "Try the demo" on the sign-in window: this copy closes and a demo one starts.
        _demo.StartRequested += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _startDemoOnExit = true;
            desktop.Shutdown();
        });

        ThemeSetup.Apply(this, settings.Current);
        settings.Changed += (_, current) => Dispatcher.UIThread.Post(() => ThemeSetup.Apply(this, current));
        WindowTheming.Register(this);
        DataGridSorting.Register();

        // Buttons for writes the token has been refused turn off (#51).
        PermissionGate.Source = _services.GetRequiredService<ILibreNmsClient>().Permissions;
        GridLayoutMemory.Settings = _services.GetRequiredService<ISettingsStore>();

        // Graylog and Unimus don't depend on the LibreNMS session.
        ConfigureGraylogIfEnabled();
        _services.GetRequiredService<AvaloniaAppActivity>().Start();

        _mainViewModel = _services.GetRequiredService<MainViewModel>();
        _mainWindow = new MainWindow(_mainViewModel, settings);
        _mainWindow.Activated += (_, _) => ScheduleWhatsNew();
        var windows = _services.GetRequiredService<WindowService>();
        windows.AttachMainWindow(_mainWindow);
        windows.ExitRequested += (_, _) => desktop.Shutdown();
        desktop.MainWindow = _mainWindow;

        // The tray (#252): closing the window can leave DashyNMS running there,
        // so it's Exit that ends the app - or closing the window when it doesn't.
        SetUpTray();
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindowLifetime.Attach(_mainWindow, () => CanLiveInTray && settings.Current.MinimiseToTrayOnClose);
        _mainWindow.Closed += (_, _) => desktop.Shutdown();

        // Nothing to sign back in with: just the sign-in window, no empty main window behind it (#231).
        var hasSavedSession = !string.IsNullOrWhiteSpace(settings.Current.ServerUrl)
                              && _services.GetRequiredService<ITokenProtector>().HasStoredToken;
        // Started hidden in the tray, with a session to restore, it stays there.
        var startHidden = settings.Current.StartMinimised
                          || args.Any(a => a.Equals("--minimised", StringComparison.OrdinalIgnoreCase) || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (hasSavedSession && !(startHidden && CanLiveInTray))
        {
            _mainWindow.Show();
        }

        // The tray follows the app's state as it changes, not just on each
        // poll (#232): signing in, failover, a ready update, an acknowledgement.
        _mainViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsConnected)
                or nameof(MainViewModel.IsSigningIn)
                or nameof(MainViewModel.IsOnBackupAddress)
                or nameof(MainViewModel.IsUpdateReady)
                or nameof(MainViewModel.HasError)
                or nameof(MainViewModel.IsTokenRejected)
                or nameof(MainViewModel.CriticalCount)
                or nameof(MainViewModel.AlertBadgeCount)
                or nameof(MainViewModel.LastUpdatedText))
            {
                ScheduleTrayUpdate();
            }
        };
        var notifications = _services.GetRequiredService<IAlertNotificationService>();
#if WINDOWS
        ToastIdentity.EnsureStartMenuShortcut(_logger);
        _services.GetRequiredService<AlertNotificationService>().Initialise();
#else
        if (_services.GetRequiredService<INativeNotificationBackend>() is MacNotificationBackend macNotifications)
        {
            try { macNotifications.Initialise(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Native notifications require the macOS app bundle"); }
        }
#endif
        notifications.ActionRequested += (_, action) => Dispatcher.UIThread.Post(() => _ = HandleNotificationActionAsync(action));
        _services.GetRequiredService<AlertMonitor>().Polled += (_, result) => Dispatcher.UIThread.Post(() =>
        {
            if (!_demo.IsActive) notifications.Handle(result);
            UpdateTrayStatus();
        });

        _mainViewModel.SignedOut += async (_, _) =>
        {
            notifications.ClearHistory();
            if (notifications is NativeAlertNotifications native)
                await Task.WhenAny(native.WhenIdle, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);

            // Leaving the demo: nothing real to wipe, just the normal app again.
            if (_demo.IsActive)
            {
                _leaveDemoOnExit = true;
                desktop.Shutdown();
                return;
            }

            _restartFresh = true;
            desktop.Shutdown();
        };

        desktop.Exit += (_, _) => OnExit();
        SetUpUpdates(desktop);

        // Restoring the session touches the network, so it must not hold up the window.
        Dispatcher.UIThread.Post(() => _ = RestoreSessionAsync(desktop), DispatcherPriority.Background);
    }

    private async Task RestoreSessionAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_services is null || _mainViewModel is null || _mainWindow is null)
        {
            return;
        }

        var session = _services.GetRequiredService<ISessionService>();
        var windows = _services.GetRequiredService<IWindowService>();

        _mainViewModel.IsSigningIn = true;
        ConnectionTestResult? restored;
        try
        {
            restored = await session.TryRestoreAsync().ConfigureAwait(true);

            // The saved session's certificate isn't trusted yet: ask here,
            // rather than dropping to sign-in and asking for the token again.
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

        if (restored is not { Succeeded: true })
        {
            if (restored is { Succeeded: false })
            {
                _logger?.LogInformation("Saved session unusable: {Error}", restored.ErrorMessage);
            }

            // Dismissing sign-in leaves the app in the tray, where there is one
            // to come back from (#252); otherwise it closes the app.
            if (!windows.ShowSignInDialog())
            {
                if (CanLiveInTray)
                {
                    _mainWindow.Hide();
                    UpdateTrayStatus();
                }
                else
                {
                    desktop.Shutdown();
                }

                return;
            }
        }

        // A restored session started hidden stays in the tray.
        if (_mainWindow.IsVisible || restored is not { Succeeded: true })
        {
            windows.ShowMain();
        }

        _services.GetRequiredService<AlertMonitor>().Start();
        _mainViewModel.OnConnected();
        ScheduleWhatsNew();
        UpdateTrayStatus();

        // Rendering the README screenshots: the tour, then close.
        if (_demo.ScreenshotFolder is { } folder && _demoFleet is not null && _logger is not null)
        {
            try
            {
                await new ScreenshotTour(folder, _mainViewModel, windows, _demoFleet, _logger).RunAsync(_mainWindow).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rendering the screenshots failed");
            }

            desktop.Shutdown();
            return;
        }

        ConfigureUnimusIfEnabled();
    }

    // ----------------------------------------------------------------- tray

    private async Task HandleNotificationActionAsync(ToastActionRequest request)
    {
        if (_mainViewModel is null || _services is null) return;
        try
        {
            if (request.Action == ToastAction.OpenUpdates)
            {
                _services.GetRequiredService<IWindowService>().ShowMain();
                _services.GetRequiredService<IWindowService>().ShowSettingsDialog(SettingsSection.About);
                return;
            }
            if (request.Action == ToastAction.InstallUpdate)
            {
                await _mainViewModel.InstallUpdateAsync().ConfigureAwait(true);
                return;
            }

            if (!_mainViewModel.IsConnected) { ShowMainOrSignIn(); return; }

            if (request.Action == ToastAction.Acknowledge && request.Alert is { } acknowledge)
                await _mainViewModel.AcknowledgeAsync(acknowledge).ConfigureAwait(true);
            else
            {
                ShowTabOrSignIn(MainTab.Alerts);
                if (request.Alert is { } alert) _mainViewModel.SelectAlert(alert);
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "A notification action failed"); }
    }

    /// <summary>Whether the app can carry on with no window open: there's a tray icon, on a platform where there's always a tray to find it in (#252).</summary>
    private bool CanLiveInTray => PlatformText.CanHideToTray && _tray?.IsShown == true && !_demo.IsRenderingScreenshots;

    private void SetUpTray()
    {
        if (_services is null || Current is not { } app)
        {
            return;
        }

        var tray = _services.GetRequiredService<TrayViewModel>();
        _tray = _services.GetRequiredService<TrayIconService>();
        _tray.Initialise(app);
        _tray.Opening += (_, _) => UpdateTrayStatus();

        // Signed out, everything but Exit leads to the sign-in window (#231).
        var windows = _services.GetRequiredService<IWindowService>();
        tray.OpenRequested += (_, _) => ShowMainOrSignIn();
        tray.DashboardRequested += (_, _) => ShowTabOrSignIn(MainTab.Dashboard);
        tray.AlertsRequested += (_, _) => ShowTabOrSignIn(MainTab.Alerts);
        tray.DevicesRequested += (_, _) => ShowTabOrSignIn(MainTab.Devices);
        tray.RefreshRequested += (_, _) => _mainViewModel?.RequestRefresh();
        tray.AlertRequested += (_, alert) =>
        {
            ShowTabOrSignIn(MainTab.Alerts);
            _mainViewModel?.SelectAlert(alert);
        };
        tray.InstallUpdateRequested += (_, _) =>
        {
            if (_mainViewModel?.InstallUpdateCommand.CanExecute(null) == true)
            {
                _mainViewModel.InstallUpdateCommand.Execute(null);
            }
        };
        tray.SettingsRequested += (_, _) =>
        {
            if (_mainViewModel?.IsConnected != true)
            {
                ShowMainOrSignIn();
                return;
            }

            windows.ShowMain();
            _mainViewModel.SettingsCommand.Execute(null);
        };
        tray.SignInOrOutRequested += (_, _) =>
        {
            // The same item reads "Sign in…" while signed out.
            if (_mainViewModel?.SignOutCommand.CanExecute(null) != true)
            {
                ShowMainOrSignIn();
                return;
            }

            windows.ShowMain();
            _mainViewModel.SignOutCommand.Execute(null);
        };
        tray.ExitRequested += (_, _) => windows.Exit();
    }

    /// <summary>A tab from the tray: there when signed in, otherwise the sign-in window (#231).</summary>
    private void ShowTabOrSignIn(MainTab tab)
    {
        if (_mainViewModel?.IsConnected == true)
        {
            _services?.GetRequiredService<IWindowService>().ShowMainTab(tab);
        }
        else
        {
            ShowMainOrSignIn();
        }
    }

    private bool _signInOpen;

    /// <summary>The tray's Open: the main window when signed in, otherwise the sign-in window (#231).</summary>
    private void ShowMainOrSignIn()
    {
        if (_services is null || _mainViewModel is null || _mainWindow is null)
        {
            return;
        }

        var windows = _services.GetRequiredService<IWindowService>();
        if (_mainViewModel.IsConnected)
        {
            windows.ShowMain();
            return;
        }

        if (_mainViewModel.IsSigningIn || _signInOpen)
        {
            return;
        }

        _signInOpen = true;
        try
        {
            if (!windows.ShowSignInDialog())
            {
                return;
            }
        }
        finally
        {
            _signInOpen = false;
        }

        windows.ShowMain();
        _services.GetRequiredService<AlertMonitor>().Start();
        _mainViewModel.OnConnected();
        ScheduleWhatsNew();
        ConfigureUnimusIfEnabled();
        UpdateTrayStatus();
    }

    /// <summary>Check at launch and while monitoring continues in the background.</summary>
    private void SetUpUpdates(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_services is null || _demo.IsActive) return;
        var updates = _services.GetRequiredService<IUpdateCheckService>();
        updates.InstallStarted += (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown());
        updates.OnStartup();
        _updateTimer = UiThread.CreateTimer(TimeSpan.FromHours(6));
        _updateTimer.Tick += (_, _) => _ = updates.CheckAsync(notifyIfNewer: true);
        _updateTimer.Start();
        _ = updates.CheckAsync(notifyIfNewer: true);
    }

    /// <summary>After an upgrade, wait for a connected, visible window before showing the notes.</summary>
    private void ScheduleWhatsNew()
    {
        if (_whatsNewScheduled || _demo.IsActive || _services is null || _signInOpen
            || _mainViewModel is not { IsConnected: true, IsSigningIn: false }
            || _mainWindow is not { IsVisible: true } || _mainWindow.WindowState == WindowState.Minimized)
        {
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>();
        var version = _services.GetRequiredService<IUpdateCheckService>().CurrentVersion;
        if (!DesktopNMS.Core.Updates.WhatsNewNotes.IsDue(BundledWhatsNew.Current, version, settings.Current.WhatsNewShownVersion)) return;

        _whatsNewScheduled = true;
        Dispatcher.UIThread.Post(() => _services.GetRequiredService<IWindowService>().ShowWhatsNew(), DispatcherPriority.Background);
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null || _mainViewModel is not { } main || _services is null)
        {
            return;
        }

        _trayUpdatePending = false;

        var connection = main.IsSigningIn
            ? TrayConnection.SigningIn
            : !main.IsConnected
                ? TrayConnection.SignedOut
                : main.IsTokenRejected
                    ? TrayConnection.TokenRejected
                    : main.HasError ? TrayConnection.Unreachable : TrayConnection.Connected;

        _services.GetRequiredService<TrayViewModel>().Update(new TraySnapshot(
            connection,
            main.IsOnBackupAddress,
            main.CriticalCount,
            // Warnings only count when Settings counts them (#269).
            main.CountFrom == DesktopNMS.Core.Models.AlertSeverity.Critical ? 0 : main.WarningCount,
            main.AcknowledgedCount,
            main.AlertBadgeCount,
            main.AlertBadgeIsCritical,
            main.LastUpdatedAt is { } at ? DateTimeOffset.Now - at : null,
            main.NextRefreshText,
            main.Alerts.ToList(),
            main.IsUpdateReady ? main.UpdateReadyText : null,
            main.CountFrom, main.UpdateActionText));
    }

    /// <summary>Several properties change together: the tray catches up once, after them.</summary>
    private void ScheduleTrayUpdate()
    {
        if (_trayUpdatePending)
        {
            return;
        }

        _trayUpdatePending = true;
        Dispatcher.UIThread.Post(UpdateTrayStatus, DispatcherPriority.Background);
    }

    private void OnExit()
    {
        _updateTimer?.Stop();
        // Stop the sole asynchronous service first, so UI-owned tray resources
        // are then disposed synchronously on this thread by the container.
        _services?.GetService<McpToolHost>()?.StopAsync().GetAwaiter().GetResult();
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _instance?.Dispose();
        _demoServer?.Dispose();

        if (_leaveDemoOnExit)
        {
            DemoMode.Reset();
        }

        if (_startDemoOnExit || _leaveDemoOnExit)
        {
            StartAnotherCopy(_startDemoOnExit ? DemoMode.Argument : null);
        }

        // Sign out (#231): a new copy wipes what this one saved once it's gone,
        // and starts at sign-in.
        if (_restartFresh && Environment.ProcessPath is { } exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { FreshStartArgument, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Couldn't start again after signing out");
            }
        }
    }

    /// <summary>Into or out of demo mode: a new copy, which waits for this one to exit.</summary>
    private void StartAnotherCopy(string? argument)
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (argument is not null)
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(AfterArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(start);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Couldn't start DashyNMS again for the demo");
        }
    }

    private static void WaitForExit(int processId)
    {
        try
        {
            using var old = Process.GetProcessById(processId);
            old.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private void ConfigureGraylogIfEnabled()
    {
        var services = _services!;
        var settings = services.GetRequiredService<ISettingsStore>().Current.Graylog;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Server))
        {
            return;
        }

        var password = services.GetRequiredService<IGraylogPasswordProtector>().Load();
        if (GraylogConnection.FromSettings(settings, password, out var error) is { } connection)
        {
            services.GetRequiredService<IGraylogApi>().Configure(connection);
        }
        else
        {
            _logger?.LogWarning("Graylog is enabled but can't be connected to: {Error}", error);
        }
    }

    private void ConfigureUnimusIfEnabled()
    {
        var services = _services!;
        var settings = services.GetRequiredService<ISettingsStore>().Current.Unimus;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Url))
        {
            return;
        }

        var token = services.GetRequiredService<IUnimusTokenProtector>().Load();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger?.LogWarning("Unimus is enabled but no API token is stored");
            return;
        }

        if (!UnimusConnection.TryParseWebRoot(settings.Url, out var webRoot, out var error) || webRoot is null)
        {
            _logger?.LogWarning("Unimus's configured URL is invalid: {Error}", error);
            return;
        }

        services.GetRequiredService<IUnimusApi>().Configure(new UnimusConnection(webRoot, token, settings.AllowUntrustedCertificate, trustedCertificates: settings.TrustedCertificates));
    }

    /// <summary>
    /// Everything the shared view models need, registered as the WPF app's
    /// App.BuildServices does - with this app's own window service, and
    /// stand-ins for what isn't here yet (PreviewPlatformServices).
    /// </summary>
    private static ServiceProvider BuildServices(DemoMode demo)
    {
        var services = new ServiceCollection();
        services.AddSingleton(demo);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddProvider(new FileLoggerProvider(LogLevel.Information));
        });

        services.AddDesktopNmsCore();

        // Secrets are encrypted per OS (#154): DPAPI on Windows, a key in the
        // macOS Keychain or the Linux Secret Service elsewhere.
        services.AddSingleton<ISecretProtector>(sp => SecretProtectors.ForThisPlatform(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ITokenProtector, TokenProtector>();
        services.AddSingleton<IUnimusTokenProtector, UnimusTokenProtector>();
        services.AddSingleton<IGraylogPasswordProtector, GraylogPasswordProtector>();
        services.AddSingleton<IChatGptCredentialStore, ChatGptCredentialStore>();
        services.AddSingleton<IIntelligenceChatStore, IntelligenceChatStore>();
        services.AddSingleton<DesktopNMS.Core.SignIn.ICertificateProbe, DesktopNMS.Core.SignIn.CertificateProbe>();

        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IConnectionState>(sp => sp.GetRequiredService<ISessionService>());
        services.AddSingleton<IDeviceCache, DeviceCache>();
        services.AddSingleton<IAlertRuleCache, AlertRuleCache>();
        services.AddSingleton<IDeviceGroupMembershipService, DeviceGroupMembershipService>();
        services.AddSingleton<IMapLayoutStore, MapLayoutStore>();
        services.AddSingleton<MapTileService>();
        services.AddSingleton<IMapTileService>(sp => sp.GetRequiredService<MapTileService>());
        services.AddSingleton<IMapTiles>(sp => sp.GetRequiredService<MapTileService>());
        services.AddSingleton<ICustomMapStore, CustomMapStore>();
        services.AddSingleton<IUnimusDeviceResolver, UnimusDeviceResolver>();
        services.AddSingleton<AvaloniaAppActivity>();
        services.AddSingleton<IAppActivity>(sp => sp.GetRequiredService<AvaloniaAppActivity>());
        services.AddSingleton<AlertMonitor>();
        services.AddSingleton<SensorMonitor>();
        services.AddSingleton<DeviceMonitor>();
#if WINDOWS
        services.AddSingleton<ITrayNotifier, WindowsBalloonNotifier>();
        services.AddSingleton<AlertNotificationService>();
        services.AddSingleton<IAlertNotificationService>(sp => sp.GetRequiredService<AlertNotificationService>());
#else
        services.AddSingleton<INativeNotificationBackend>(sp => OperatingSystem.IsMacOS()
            ? new MacNotificationBackend(sp.GetRequiredService<ILogger<MacNotificationBackend>>())
            : new LinuxNotificationBackend(sp.GetRequiredService<ILogger<LinuxNotificationBackend>>()));
        services.AddSingleton<IAlertNotificationService, NativeAlertNotifications>();
#endif
        services.AddSingleton<IStartupRegistration, PlatformStartupRegistration>();
        services.AddSingleton<IUpdateInstallation, PlatformUpdateInstallation>();
        services.AddSingleton<IUpdateAnnouncements, PlatformUpdateAnnouncements>();
        services.AddSingleton<IUpdateCheckService, DesktopUpdateService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowService>(sp => sp.GetRequiredService<WindowService>());
        services.AddSingleton<ISelfActionTracker, SelfActionTracker>();
        services.AddSingleton<NotificationRuleService>();
        services.AddSingleton<IDashboardLayoutService, DashboardLayoutService>();
        services.AddSingleton<IFleetLinks, FleetLinks>();
        services.AddSingleton<IFleetPorts, FleetPorts>();
        services.AddSingleton<INeighbourDirectory, NeighbourDirectory>();
        services.AddSingleton<McpToolHost>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<DeviceListViewModel>();
        services.AddSingleton<HealthViewModel>();
        services.AddSingleton<IntelligenceViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton<LocationsViewModel>();
        services.AddSingleton<NeighboursViewModel>();
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
        services.AddTransient<NeighbourViewEditorViewModel>();
        services.AddTransient<MaintenanceScheduleViewModel>();
        services.AddTransient<RuleEditorViewModel>();
        services.AddTransient<AlertTemplateEditorViewModel>();
        return services.BuildServiceProvider();
    }
}
