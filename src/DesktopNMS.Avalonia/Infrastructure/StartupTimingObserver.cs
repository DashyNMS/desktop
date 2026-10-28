using Avalonia.Controls;
using Avalonia.Threading;
using DesktopNMS.Diagnostics;

namespace DesktopNMS.Infrastructure;

public static class StartupTimingObserver
{
    public static void Observe(Window window, StartupTiming timing, IAppMetrics metrics, Func<bool> consentedAtStartup)
    {
        window.Opened += (_, _) => Dispatcher.UIThread.Post(
            () => timing.Ready(metrics, consentedAtStartup(), MetricMode.Interactive), DispatcherPriority.Loaded);
    }
}
