using DesktopNMS.Core.Api;

namespace DesktopNMS.Diagnostics;

/// <summary>Attaches only while consent is active; the transport never exposes request data.</summary>
public static class ApiMetrics
{
    public static void Configure(LibreNmsTransport transport, AppMetrics metrics)
    {
        transport.RequestObserver = null;
        if (!metrics.IsEnabled) return;
        var generation = metrics.ConsentGeneration;
        transport.RequestObserver = point =>
        {
            if (!Enum.IsDefined(point.Operation) || !Enum.IsDefined(point.Outcome) || point.Duration < TimeSpan.Zero) return;
            var area = point.Operation switch
            {
                ApiOperation.DeviceList => MetricArea.DeviceList,
                ApiOperation.Alerts => MetricArea.Alerts,
                ApiOperation.Sensors => MetricArea.Sensors,
                _ => MetricArea.ApiOther,
            };
            var outcome = point.Outcome switch
            {
                ApiRequestOutcome.Success => MetricOutcome.Success,
                ApiRequestOutcome.ClientError => MetricOutcome.ClientError,
                ApiRequestOutcome.ServerError => MetricOutcome.ServerError,
                ApiRequestOutcome.Timeout => MetricOutcome.Timeout,
                ApiRequestOutcome.NetworkFailure => MetricOutcome.NetworkFailure,
                ApiRequestOutcome.Cancelled => MetricOutcome.Cancelled,
                _ => MetricOutcome.Failed,
            };
            metrics.Record(new(MetricName.ApiRequestCount, 1, Outcome: outcome, Area: area), generation);
            metrics.Record(new(MetricName.ApiDuration, point.Duration.TotalMilliseconds, Outcome: outcome, Area: area), generation);
        };
    }
}
