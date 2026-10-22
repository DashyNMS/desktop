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
    private DemoMode _demo = new(isActive: false);
    private DemoServer? _demoServer;
    private DemoFleet? _demoFleet;
    private bool _startDemoOnExit;
    private bool _leaveDemoOnExit;

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

        // Its own folder inside the usual one, so this preview never touches
        // the WPF app's settings, token or notification state - or the demo's,
        // started empty each time. Secrets saved here are encrypted per OS (#154).
        if (_demo.IsActive)
        {
            DemoMode.Reset();
        }

        AppPaths.UseDataDirectory(_demo.IsActive ? DemoMode.DataDirectory : Path.Combine(AppPaths.DefaultDataDirectory, "avalonia"));

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
        var windows = _services.GetRequiredService<WindowService>();
        windows.AttachMainWindow(_mainWindow);
        windows.ExitRequested += (_, _) => desktop.Shutdown();
        desktop.MainWindow = _mainWindow;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Nothing to sign back in with: just the sign-in window, no empty main window behind it (#231).
        var hasSavedSession = !string.IsNullOrWhiteSpace(settings.Current.ServerUrl)
                              && _services.GetRequiredService<ITokenProtector>().HasStoredToken;
        if (hasSavedSession)
        {
            _mainWindow.Show();
        }

        _mainViewModel.SignedOut += (_, _) =>
        {
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

            // There's no tray yet (#252) to come back from, so closing sign-in closes the app.
            if (!windows.ShowSignInDialog())
            {
                desktop.Shutdown();
                return;
            }
        }

        windows.ShowMain();
        _services.GetRequiredService<AlertMonitor>().Start();
        _mainViewModel.OnConnected();

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

    private void OnExit()
    {
        _services?.Dispose();
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
        services.AddSingleton<IAlertNotificationService, NoAlertNotifications>();
        services.AddSingleton<IStartupRegistration, NoStartupRegistration>();
        services.AddSingleton<IUpdateCheckService, NoUpdates>();
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
