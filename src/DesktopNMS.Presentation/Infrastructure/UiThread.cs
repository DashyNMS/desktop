namespace DesktopNMS.Infrastructure;

/// <summary>How urgently work queued on the UI thread should run.</summary>
public enum UiPriority
{
    /// <summary>As soon as the UI thread is free - the usual case.</summary>
    Normal,

    /// <summary>After input and rendering - for rebuilds that can wait a frame.</summary>
    Background,
}

/// <summary>
/// The UI thread, without naming a UI framework (#249): WPF's Dispatcher
/// today, Avalonia's next. View models capture <see cref="UiThread.Dispatcher"/>
/// and marshal results from background polls onto it.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when called on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> on the UI thread; the task completes when it has.</summary>
    Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal);

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal);

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting for it.</summary>
    void Post(Action action, UiPriority priority = UiPriority.Normal);
}

/// <summary>A timer that ticks on the UI thread - DispatcherTimer's shape, without WPF.</summary>
public interface IUiTimer
{
    TimeSpan Interval { get; set; }

    bool IsEnabled { get; }

    /// <summary>Raised on the UI thread every <see cref="Interval"/> while started.</summary>
    event EventHandler? Tick;

    void Start();

    void Stop();
}

/// <summary>
/// The front end's UI thread, set once at startup before any view model is
/// created (the WPF app does it in App.OnStartup). Until then - in tests, for
/// example - work runs immediately on the calling thread and timers never tick.
/// </summary>
public static class UiThread
{
    private static IUiDispatcher _dispatcher = ImmediateDispatcher.Instance;
    private static Func<TimeSpan, UiPriority, IUiTimer> _timers = (interval, _) => new InertTimer { Interval = interval };

    public static IUiDispatcher Dispatcher => _dispatcher;

    /// <summary>Called once by the front end at startup.</summary>
    public static void Use(IUiDispatcher dispatcher, Func<TimeSpan, UiPriority, IUiTimer> timers)
    {
        _dispatcher = dispatcher;
        _timers = timers;
    }

    /// <summary>A stopped timer on the UI thread; call <see cref="IUiTimer.Start"/> to run it.</summary>
    public static IUiTimer CreateTimer(TimeSpan interval, UiPriority priority = UiPriority.Normal) => _timers(interval, priority);

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public static readonly ImmediateDispatcher Instance = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal) => Task.FromResult(func());

        public void Post(Action action, UiPriority priority = UiPriority.Normal) => action();
    }

    private sealed class InertTimer : IUiTimer
    {
        public TimeSpan Interval { get; set; }

        public bool IsEnabled { get; private set; }

        public event EventHandler? Tick
        {
            add { }
            remove { }
        }

        public void Start() => IsEnabled = true;

        public void Stop() => IsEnabled = false;
    }
}
