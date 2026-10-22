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

    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private bool _restartFresh;

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
        // Its own folder inside the usual one, so this preview never touches
        // the WPF app's settings, token or notification state. Secrets saved
        // here are encrypted per OS (#154).
        AppPaths.UseDataDirectory(Path.Combine(AppPaths.DefaultDataDirectory, "avalonia"));

        // The fresh copy after Sign out (#231): once the closing copy has
        // really gone, everything it saved goes too.
        var args = desktop.Args ?? [];
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

        _services = BuildServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("DashyNMS (cross-platform preview) starting on {OS}", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        AsyncRelayCommand.UnhandledError += (_, ex) => _logger.LogError(ex, "A command failed");

        var settings = _services.GetRequiredService<ISettingsStore>();
        settings.Load();
        ThemeSetup.Apply(this, settings.Current);
        settings.Changed += (_, current) => Dispatcher.UIThread.Post(() => ThemeSetup.Apply(this, current));
        WindowTheming.Register(this);

        // Buttons for writes the token has been refused turn off (#51).
        PermissionGate.Source = _services.GetRequiredService<ILibreNmsClient>().Permissions;

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
        ConfigureUnimusIfEnabled();
    }

    private void OnExit()
    {
        _services?.Dispose();

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
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DemoMode(isActive: false));

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
        services.AddSingleton<IMapTiles, NoMapTiles>();
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
        return services.BuildServiceProvider();
    }
}
