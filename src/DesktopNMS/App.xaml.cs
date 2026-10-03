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
using DesktopNMS.Core.Updates;
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


    /// <summary>Set by Sign out (#231): on the way out, wipe everything saved and start a fresh copy.</summary>
    private bool _resetOnExit;

    /// <summary>Passed to the fresh copy after Sign out, with the closing copy's process id (#231).</summary>
    private const string FreshStartArgument = "--fresh-start";

    /// <summary>Passed by the uninstaller (#64): remove the notification registration, then exit.</summary>
    private const string UninstallArgument = "--uninstall";
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

        // Before the single-instance check: this runs alongside nothing else,
        // shows nothing, and must work even if a copy is somehow still open.
        if (e.Args.Any(a => a.Equals(UninstallArgument, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveNotificationRegistration();
            Shutdown();
            return;
        }

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

        // The fresh copy after Sign out (#231): once the closing copy has
        // really gone, delete again before anything is loaded.
        var fresh = Array.FindIndex(e.Args, a => a.Equals(FreshStartArgument, StringComparison.OrdinalIgnoreCase));
        if (fresh >= 0)
        {
            var exited = fresh + 1 < e.Args.Length && int.TryParse(e.Args[fresh + 1], out var oldProcessId)
                ? WaitForExit(oldProcessId)
                : true;
            RecordReset(exited ? "fresh copy" : "fresh copy (closing copy still running after 20 s)", WipeSavedData());
        }

        _services = BuildServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("DashyNMS starting");

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

        // Buttons for writes the token has been refused turn off (#51).
        PermissionGate.Source = _services.GetRequiredService<ILibreNmsClient>().Permissions;

        SetUpTray();
        SetUpCertificatePrompt();
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

        // The tray follows the app's state as it changes, not just on each
        // poll (#232): signing in, failover, a ready update, an acknowledgement.
        _mainViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsConnected)
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

            if (args.PropertyName is nameof(MainViewModel.IsConnected) && _mainViewModel.IsConnected)
            {
                ScheduleWhatsNew();
            }
        };

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
        ScheduleWhatsNew();
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

        // A fresh install has nothing new to it (#227). Before OnStartup, so
        // it's judged on the settings as they were found.
        var settings = _services.GetRequiredService<ISettingsStore>();
        if (WhatsNewNotes.MarkSeenIfFreshInstall(settings.Current, updates.CurrentVersion))
        {
            settings.SaveQuietly();
        }

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

    private bool _whatsNewScheduled;

    /// <summary>
    /// The first launch of a new release, once connected (#227): its "What's
    /// new", over the main window - never over sign-in, and not while the app
    /// sits in the tray, where it waits for the window to be opened.
    /// </summary>
    private void ScheduleWhatsNew()
    {
        if (_whatsNewScheduled || _services is null || _mainViewModel?.IsConnected != true)
        {
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore>();
        var version = _services.GetRequiredService<IUpdateCheckService>().CurrentVersion;
        if (!WhatsNewNotes.IsDue(BundledWhatsNew.Current, version, settings.Current.WhatsNewShownVersion))
        {
            return;
        }

        _whatsNewScheduled = true;
        _ = ShowWhatsNewWhenReadyAsync();
    }

    private async Task ShowWhatsNewWhenReadyAsync()
    {
        // Let the connection settle and the sign-in window close first.
        while (!_isShuttingDown && (_signInOpen || _mainViewModel?.IsSigningIn == true || _mainWindow is not { IsVisible: true, WindowState: not WindowState.Minimized }))
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }

        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        if (!_isShuttingDown)
        {
            _services?.GetRequiredService<IWindowService>().ShowWhatsNew();
        }
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
        services.AddSingleton<TrayViewModel>();
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

        var tray = _services.GetRequiredService<TrayViewModel>();
        _tray = _services.GetRequiredService<TrayIconService>();
        _tray.Initialise();
        _tray.Opening += (_, _) => UpdateTrayStatus();

        // Signed out, everything but Exit leads to the sign-in window (#231).
        tray.OpenRequested += (_, _) => ShowMainOrSignIn();
        tray.DashboardRequested += (_, _) => ShowTabOrSignIn(MainTab.Dashboard);
        tray.AlertsRequested += (_, _) => ShowTabOrSignIn(MainTab.Alerts);
        tray.DevicesRequested += (_, _) => ShowTabOrSignIn(MainTab.Devices);
        tray.RefreshRequested += (_, _) => _mainViewModel?.RequestRefresh();
        tray.AlertRequested += (_, alertId) =>
        {
            ShowTabOrSignIn(MainTab.Alerts);
            _mainViewModel?.SelectAlert(alertId);
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

            _services.GetRequiredService<IWindowService>().ShowMain();
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

            _services.GetRequiredService<IWindowService>().ShowMain();
            _mainViewModel.SignOutCommand.Execute(null);
        };
        tray.ExitRequested += (_, _) => ShutdownApplication();
    }

    /// <summary>A tab from the tray: there when signed in, otherwise the sign-in window (#231).</summary>
    private void ShowTabOrSignIn(MainTab tab)
    {
        if (_services is null)
        {
            return;
        }

        if (_mainViewModel?.IsConnected == true)
        {
            _services.GetRequiredService<IWindowService>().ShowMainTab(tab);
        }
        else
        {
            ShowMainOrSignIn();
        }
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
            ScheduleWhatsNew();
            UpdateTrayStatus();
        }
    }

    /// <summary>
    /// After Sign out (#231): close, wipe everything saved on this computer
    /// once closed (see <see cref="OnExit"/>), and start a fresh copy - so
    /// nothing from the server, nor any setting, is left. The fresh copy has
    /// nothing to sign in with, so it opens on the sign-in window alone.
    /// </summary>
    private void RestartSignedOut()
    {
        _logger?.LogInformation("Signed out: clearing everything saved and restarting to the sign-in window");

        // "Start with Windows" lives in the registry, not the data folder.
        _services?.GetService<IStartupRegistration>()?.SetEnabled(false);

        _resetOnExit = true;
        ShutdownApplication();
    }

    /// <summary>Everything saved on this computer, gone (#231) - see <see cref="LocalDataReset"/>.</summary>
    private static IReadOnlyList<string> WipeSavedData()
        => LocalDataReset.Wipe(AppPaths.DataDirectory, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashyNMS"));

    /// <summary>
    /// What a sign-out reset did, in the logs folder (which it keeps) - written
    /// directly, as the closing copy has no logger left by then.
    /// </summary>
    private static void RecordReset(string who, IReadOnlyList<string> failed)
    {
        try
        {
            var settingsLeft = System.IO.File.Exists(AppPaths.SettingsFile);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {who}: {(failed.Count == 0 ? "removed everything" : "could not remove " + string.Join("; ", failed))}; settings.json {(settingsLeft ? "STILL THERE" : "gone")}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppPaths.LogDirectory, "sign-out-reset.log"), line);
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }

    /// <summary>Waits (up to 20 s) for the copy that signed out to finish exiting.</summary>
    private static bool WaitForExit(int processId)
    {
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(processId);
            return old.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (ArgumentException)
        {
            return true; // Already gone.
        }
    }

    private void UpdateTrayStatus()
    {
        if (_tray is null || _mainViewModel is null)
        {
            return;
        }

        _trayUpdatePending = false;

        var main = _mainViewModel;
        var connection = main.IsSigningIn
            ? TrayConnection.SigningIn
            : !main.IsConnected
                ? TrayConnection.SignedOut
                : main.IsTokenRejected
                    ? TrayConnection.TokenRejected
                    : main.HasError ? TrayConnection.Unreachable : TrayConnection.Connected;

        _services?.GetRequiredService<TrayViewModel>().Update(new TraySnapshot(
            connection,
            main.IsOnBackupAddress,
            main.CriticalCount,
            main.WarningCount,
            main.AcknowledgedCount,
            main.AlertBadgeCount,
            main.AlertBadgeIsCritical,
            main.LastUpdatedAt is { } at ? DateTimeOffset.Now - at : null,
            main.NextRefreshText,
            main.Alerts.ToList(),
            main.IsUpdateReady ? main.UpdateReadyText : null));
    }

    private bool _trayUpdatePending;

    /// <summary>Several properties change together: the tray catches up once, after them.</summary>
    private void ScheduleTrayUpdate()
    {
        if (_trayUpdatePending)
        {
            return;
        }

        _trayUpdatePending = true;
        Dispatcher.InvokeAsync(UpdateTrayStatus, DispatcherPriority.Background);
    }

    // ----------------------------------------------------------- certificates

    private bool _certificatePromptOpen;
    private readonly HashSet<string> _declinedCertificates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A request failed on a certificate that isn't trusted yet while signed
    /// in - typically the backup address's, the first time the app fails over
    /// to it. Ask there and then, with its fingerprint, rather than leaving an
    /// error with nowhere to check and accept it. Once per certificate a
    /// session if turned down; the many requests failing on it at once only
    /// ask once.
    /// </summary>
    private void SetUpCertificatePrompt()
    {
        if (_services is null)
        {
            return;
        }

        var client = _services.GetRequiredService<ILibreNmsClient>();
        client.CertificateRejected += (_, certificate) => Dispatcher.InvokeAsync(() =>
        {
            if (_certificatePromptOpen
                || _isShuttingDown
                || _mainViewModel?.IsConnected != true
                || _declinedCertificates.Contains(certificate.Fingerprint))
            {
                return;
            }

            _certificatePromptOpen = true;
            try
            {
                var windows = _services.GetRequiredService<IWindowService>();
                var service = client.Failover.IsOnBackup ? "LibreNMS (backup address)" : "LibreNMS";
                if (windows.ConfirmTrustCertificate(service, certificate))
                {
                    _services.GetRequiredService<ISessionService>().TrustCertificate(certificate);
                    _mainViewModel?.RequestRefresh();
                }
                else
                {
                    _declinedCertificates.Add(certificate.Fingerprint);
                }
            }
            finally
            {
                _certificatePromptOpen = false;
            }
        });
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

    /// <summary>
    /// The uninstaller's last call into the app (#64): clears its toasts from
    /// Action Center and removes the COM activator and AppUserModelId it
    /// registered under HKCU, so nothing is left pointing at a deleted exe.
    /// The installer removes the shortcut and the Run value itself.
    /// </summary>
    private static void RemoveNotificationRegistration()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception)
        {
            // Nothing to show and nobody to tell - the uninstall carries on.
        }
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

        // Sign out (#231): only now - with every window closed, every service
        // disposed and every last save written - is it safe to wipe, then
        // start the fresh copy, which finds nothing and opens on sign-in.
        if (_resetOnExit && Environment.ProcessPath is { Length: > 0 } path)
        {
            var failed = WipeSavedData();
            RecordReset("closing copy", failed);

            try
            {
                // The fresh copy deletes again once this one has gone, in case
                // anything here was still holding a file open.
                var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false };
                start.ArgumentList.Add(FreshStartArgument);
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex)
            {
                RecordReset("closing copy could not start the fresh copy: " + ex.Message, Array.Empty<string>());
            }
        }

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
