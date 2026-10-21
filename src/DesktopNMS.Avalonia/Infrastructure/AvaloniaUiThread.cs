using Avalonia.Threading;

namespace DesktopNMS.Infrastructure;

/// <summary>Avalonia's side of <see cref="UiThread"/>: its UI-thread Dispatcher and DispatcherTimer (#153), as WpfUiThread is WPF's.</summary>
public static class AvaloniaUiThread
{
    /// <summary>Points <see cref="UiThread"/> at Avalonia's UI thread. Call before creating any view model.</summary>
    public static void Install()
        => UiThread.Use(new AvaloniaUiDispatcher(), (interval, priority) => new AvaloniaUiTimer(interval, priority));

    private static DispatcherPriority Map(UiPriority priority)
        => priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal;

    private sealed class AvaloniaUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

        public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
            => Dispatcher.UIThread.InvokeAsync(action, Map(priority)).GetTask();

        public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
            => Dispatcher.UIThread.InvokeAsync(func, Map(priority)).GetTask();

        public void Post(Action action, UiPriority priority = UiPriority.Normal)
            => Dispatcher.UIThread.Post(action, Map(priority));
    }

    private sealed class AvaloniaUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public AvaloniaUiTimer(TimeSpan interval, UiPriority priority)
        {
            _timer = new DispatcherTimer(Map(priority)) { Interval = interval };
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
