using System.Windows.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>WPF's side of <see cref="UiThread"/>: the Dispatcher and DispatcherTimer (#249).</summary>
public static class WpfUiThread
{
    /// <summary>Points <see cref="UiThread"/> at this (the UI) thread's Dispatcher. Call before creating any view model.</summary>
    public static void Install(Dispatcher dispatcher)
        => UiThread.Use(new WpfUiDispatcher(dispatcher), (interval, priority) => new WpfUiTimer(dispatcher, interval, priority));

    private static DispatcherPriority Map(UiPriority priority)
        => priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal;

    private sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public bool CheckAccess() => dispatcher.CheckAccess();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
            => dispatcher.InvokeAsync(action, Map(priority)).Task;

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
            => dispatcher.InvokeAsync(func, Map(priority)).Task;

        public void Post(Action action, UiPriority priority = UiPriority.Normal)
            => dispatcher.BeginInvoke(action, Map(priority));
    }

    private sealed class WpfUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public WpfUiTimer(Dispatcher dispatcher, TimeSpan interval, UiPriority priority)
        {
            _timer = new DispatcherTimer(Map(priority), dispatcher) { Interval = interval };
            _timer.Tick += (_, e) => Tick?.Invoke(this, e);
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsEnabled => _timer.IsEnabled;

        public event EventHandler? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}
