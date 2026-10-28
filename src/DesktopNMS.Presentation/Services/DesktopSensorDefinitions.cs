using DesktopNMS.Core.Sensors;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>Desktop's installation default, independent of account data and other Core consumers.</summary>
public static class DesktopSensorDefinitions
{
    public static async Task InitialiseAsync(SensorDefinitionStore store, ILogger logger, bool enableByDefault = true)
    {
        await store.InitialiseAsync().ConfigureAwait(false);
        // An existing file (including a disabled preference or a damaged file) belongs to
        // the user. Only an installation without a recorded choice gets the default.
        if (!enableByDefault || File.Exists(store.Paths.SettingsFile)) return;
        try
        {
            await store.ConfigureAsync(new SensorDefinitionOptions { Enabled = true }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A read-only installation must still be able to launch. Core exposes the
            // failure in Settings; avoid logging paths or custom source details here.
            logger.LogWarning("Could not save the sensor definitions default ({ErrorType})", ex.GetType().Name);
        }
    }
}
