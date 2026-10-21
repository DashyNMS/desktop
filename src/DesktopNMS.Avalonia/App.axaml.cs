using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DesktopNMS.Core;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Security;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DesktopNMS;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Its own folder inside the usual one, so this preview never
            // touches the WPF app's settings, token or notification state.
            // Secrets saved here are encrypted per OS (#154).
            AppPaths.UseDataDirectory(Path.Combine(AppPaths.DefaultDataDirectory, "avalonia"));

            // Before any view model: they capture the UI thread on construction.
            AvaloniaUiThread.Install();

            _services = BuildServices();
            var scaffold = _services.GetRequiredService<ScaffoldViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = scaffold };
            _ = scaffold.RestoreAsync();
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The services the scaffold needs, registered as the WPF app's App.BuildServices does.</summary>
    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
        });

        services.AddDesktopNmsCore();

        // Secrets are encrypted per OS (#154): DPAPI on Windows, a key in the
        // macOS Keychain or the Linux Secret Service elsewhere.
        services.AddSingleton<ISecretProtector>(sp => SecretProtectors.ForThisPlatform(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ITokenProtector, TokenProtector>();
        services.AddSingleton<IUnimusTokenProtector, UnimusTokenProtector>();
        services.AddSingleton<IGraylogPasswordProtector, GraylogPasswordProtector>();
        services.AddSingleton<DesktopNMS.Core.Intelligence.IChatGptCredentialStore, ChatGptCredentialStore>();
        services.AddSingleton<DesktopNMS.Core.Intelligence.IIntelligenceChatStore, IntelligenceChatStore>();

        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IConnectionState>(sp => sp.GetRequiredService<ISessionService>());
        services.AddSingleton<IDeviceCache, DeviceCache>();
        services.AddSingleton<AlertMonitor>();
        services.AddSingleton<ScaffoldViewModel>();
        return services.BuildServiceProvider();
    }
}
