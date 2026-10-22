using DesktopNMS.Core.Services;

namespace DesktopNMS.Infrastructure;

internal static class SensorRefreshStatus
{
    public static string Failure(SensorPollResult result, DateTimeOffset? lastGood)
    {
        var reason = result.IsTimeout ? "LibreNMS took too long to send the sensor list" : result.ErrorMessage ?? "The sensor refresh failed";
        var observed = result.ObservedAt ?? lastGood;
        return observed is { } time
            ? $"{reason} - showing readings from {time.LocalDateTime:HH:mm}."
            : reason + ". Try refreshing again.";
    }
}
