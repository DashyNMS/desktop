using System.Runtime.Versioning;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopNMS.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LibreNMS client, settings and secret storage. The UI layer
    /// adds its own services on top.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddDesktopNmsCore(this IServiceCollection services)
    {
        services.AddSingleton<LibreNmsTransport>();
        services.AddSingleton<ILibreNmsTransport>(sp => sp.GetRequiredService<LibreNmsTransport>());
        services.AddSingleton<LibreNmsClient>();
        services.AddSingleton<ILibreNmsClient>(sp => sp.GetRequiredService<LibreNmsClient>());

        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<INotificationStateStore, NotificationStateStore>();
        services.AddSingleton<ITokenProtector, DpapiTokenProtector>();

        // Unimus (issue #115): a second, independent integration with its own
        // lifetime, configured/cleared from Settings rather than alongside
        // LibreNMS sign-in - see UnimusApi's own remarks.
        services.AddSingleton<UnimusApi>();
        services.AddSingleton<IUnimusApi>(sp => sp.GetRequiredService<UnimusApi>());
        services.AddSingleton<IUnimusTokenProtector, DpapiUnimusTokenProtector>();

        services.AddSingleton<IGitHubReleaseService, GitHubReleaseService>();

        return services;
    }
}
