using System;
using System.Windows.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Ticks once a second so a consumer can refresh a live countdown display
/// (e.g. a tab's "NextRefreshText"). Deliberately holds no countdown state of
/// its own - it is purely a "please recompute your display now" nudge. An
/// earlier version tracked its own remaining-seconds counter, seeded and
/// periodically resynced from each poll; keeping two independent sources of
/// truth (a local counter here, and the shared monitor's actual aligned
/// schedule - see <see cref="DesktopNMS.Services.PollAlignment"/>) meant they
/// could drift out of step with each other, and with whatever other tab was
/// showing the same countdown. A consumer should instead compute its display
/// text fresh from the real schedule every time this ticks.
/// </summary>
public sealed class AutoRefreshTimer : IDisposable
{
    private readonly DispatcherTimer _timer;

    public AutoRefreshTimer(Action onTick)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => onTick();
    }

    /// <summary>Starts ticking, if not already running. Safe to call repeatedly.</summary>
    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Dispose() => _timer.Stop();
}
