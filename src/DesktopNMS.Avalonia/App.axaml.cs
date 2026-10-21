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
            AppPaths.UseDataDirectory(Path.Combine(AppPaths.DefaultDataDirectory, "avalonia"));

            // Before any view model: they capture the UI thread on construction.
            AvaloniaUiThread.Install();

            _services = BuildServices();
            desktop.MainWindow = new MainWindow { DataContext = _services.GetRequiredService<ScaffoldViewModel>() };
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

        // Secret storage per OS is #154; until then the token lasts the run.
        services.AddSingleton<ITokenProtector, SessionOnlyTokenProtector>();

        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IConnectionState>(sp => sp.GetRequiredService<ISessionService>());
        services.AddSingleton<IDeviceCache, DeviceCache>();
        services.AddSingleton<AlertMonitor>();
        services.AddSingleton<ScaffoldViewModel>();
        return services.BuildServiceProvider();
    }
}
