using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DesktopNMS.Infrastructure;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class MainWindowLifetimeTests
{
    [AvaloniaFact]
    public void Fullscreen_close_waits_for_native_completion_and_coalesces_repeated_closes()
    {
        var window = new Window();
        var hide = true;
        Action completed = null!;
        var observer = new Observer();
        var subscriptions = 0;
        MainWindowLifetime.Attach(window, () => hide, true, callback =>
        {
            subscriptions++;
            completed = callback;
            return observer;
        });
        window.Show();
        window.WindowState = WindowState.FullScreen;
        window.Close();
        window.Close();
        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(1, subscriptions);
        completed();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.True(observer.Disposed);
        window.Show();
        Assert.True(window.IsVisible);
        hide = false;
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Other_platforms_and_windowed_closes_hide_immediately(bool mac, bool fullscreen)
    {
        var window = new Window();
        var hide = true;
        MainWindowLifetime.Attach(window, () => hide, mac,
            _ => throw new InvalidOperationException("Should not observe fullscreen exit."));
        window.Show();
        if (fullscreen) window.WindowState = WindowState.FullScreen;
        window.Close();
        Assert.False(window.IsVisible);
        hide = false;
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_with_setting_disabled_cancels_pending_hide()
    {
        var window = new Window();
        var hide = true;
        Action completed = null!;
        var observer = new Observer();
        var closed = false;
        MainWindowLifetime.Attach(window, () => hide, true, callback =>
        {
            completed = callback;
            return observer;
        });
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.WindowState = WindowState.FullScreen;
        window.Close();
        completed(); // Completion queued, then a real close wins.
        hide = false;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
        Assert.True(observer.Disposed);
    }

    [AvaloniaTheory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public void Quit_and_shutdown_do_not_cancel_closing_or_hide(WindowCloseReason reason)
    {
        var window = new TestWindow();
        Action completed = null!;
        var observer = new Observer();
        MainWindowLifetime.Attach(window, () => true, true, callback =>
        {
            completed = callback;
            return observer;
        });
        window.Show();
        window.WindowState = WindowState.FullScreen;
        window.Close();
        // Avalonia exposes the reasons but keeps this constructor internal.
        var closing = (WindowClosingEventArgs)Activator.CreateInstance(
            typeof(WindowClosingEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { reason, true }, null)!;
        window.RaiseClosing(closing);
        Assert.False(closing.Cancel);
        Assert.True(observer.Disposed);
        completed();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        window.Hide();
    }

    private sealed class TestWindow : Window
    {
        public void RaiseClosing(WindowClosingEventArgs args) => OnClosing(args);
    }

    private sealed class Observer : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
