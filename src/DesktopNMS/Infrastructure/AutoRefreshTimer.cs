using System;
using System.Windows.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Counts down to a tab's next automatic refresh and invokes it when due,
/// re-reading the interval each time so a Settings change takes effect on the
/// next cycle. Used by tabs (Devices, Health) that otherwise only load once
/// when first shown, so they keep polling in the background afterwards the
/// same way the Alerts tab already does.
/// </summary>
public sealed class AutoRefreshTimer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<int> _intervalSecondsProvider;
    private readonly Action _onDue;

    public AutoRefreshTimer(Func<int> intervalSecondsProvider, Action onDue)
    {
        _intervalSecondsProvider = intervalSecondsProvider;
        _onDue = onDue;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    /// <summary>Raised every second while running, and whenever <see cref="Reset"/> changes the target.</summary>
    public event EventHandler? RemainingChanged;

    public int SecondsRemaining { get; private set; }

    /// <summary>A short "45s" / "2:05" form for a status bar.</summary>
    public string RemainingText
    {
        get
        {
            if (SecondsRemaining <= 0)
            {
                return "due";
            }

            var span = TimeSpan.FromSeconds(SecondsRemaining);
            return span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}:{span.Seconds:00}"
                : $"{SecondsRemaining}s";
        }
    }

    /// <summary>Starts ticking, if not already running. Safe to call repeatedly.</summary>
    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }

        Reset();
        _timer.Start();
    }

    /// <summary>Restarts the countdown from a full interval, e.g. after a manual refresh completes.</summary>
    public void Reset()
    {
        SecondsRemaining = Math.Max(1, _intervalSecondsProvider());
        RemainingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        SecondsRemaining--;

        if (SecondsRemaining <= 0)
        {
            Reset();
            _onDue();
        }
        else
        {
            RemainingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
