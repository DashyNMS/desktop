using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopNMS.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LibreNMS, Unimus and Graylog clients and settings. Secret
    /// storage (<see cref="ITokenProtector"/>, <see cref="IUnimusTokenProtector"/>,
    /// <see cref="IGraylogPasswordProtector"/>) is platform-specific, so each app
    /// registers its own - which keeps Core free of Windows-only code (#152).
    /// </summary>
    public static IServiceCollection AddDesktopNmsCore(this IServiceCollection services)
    {
        services.AddSingleton<ServerFailover>();
        services.AddSingleton<LibreNmsTransport>();
        services.AddSingleton<ILibreNmsTransport>(sp => sp.GetRequiredService<LibreNmsTransport>());
        services.AddSingleton<LibreNmsClient>();
        services.AddSingleton<ILibreNmsClient>(sp => sp.GetRequiredService<LibreNmsClient>());

        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<INotificationStateStore, NotificationStateStore>();

        // Unimus (issue #115): a second, independent integration with its own
        // lifetime, configured/cleared from Settings rather than alongside
        // LibreNMS sign-in - see UnimusApi's own remarks.
        services.AddSingleton<UnimusApi>();
        services.AddSingleton<IUnimusApi>(sp => sp.GetRequiredService<UnimusApi>());

        // Graylog (issue #114): the same independent, Settings-driven lifetime
        // as Unimus - see GraylogApi's own remarks.
        services.AddSingleton<GraylogApi>();
        services.AddSingleton<IGraylogApi>(sp => sp.GetRequiredService<GraylogApi>());

        services.AddSingleton<IGitHubReleaseService, GitHubReleaseService>();

        return services;
    }
}
