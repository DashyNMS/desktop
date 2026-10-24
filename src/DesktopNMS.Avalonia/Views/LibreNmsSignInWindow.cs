using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Services;

namespace DesktopNMS.Views;

/// <summary>A throwaway native web session; no password, cookie or token is logged.</summary>
public sealed class LibreNmsSignInWindow : Window
{
    private readonly NativeWebView _web = new();
    private readonly WebSignInSession _session;
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "DashyNMS-SignIn-" + Guid.NewGuid().ToString("N"));
    private bool _finished;

    public LibreNmsSignInWindow(WebSignInRequest request)
    {
        Dispatcher.UIThread.UnhandledException += OnBrowserInitialisationError;
        Title = "Sign in with LibreNMS";
        Width = 900; Height = 720; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (OperatingSystem.IsLinux()) _web.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        else if (OperatingSystem.IsWindows()) _web.AdapterPreference = [WebViewAdapterType.WebView2];
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        cancel.Click += (_, _) => Finish(WebSignInResult.Cancelled);
        var layout = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Bottom); layout.Children.Add(cancel); layout.Children.Add(_web); Content = layout;
        _session = new(request.Flow, () => _web.Source, script => _web.InvokeScript(script), uri => _web.Navigate(uri), Finish);
        _web.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            switch (args)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs windows:
                    windows.IsInPrivateModeEnabled = true; windows.UserDataFolder = _profile; break;
                case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                    apple.NonPersistentDataStore = true; break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.EphemeralDataManager = true; gtk.DisableCache = true; break;
            }
        };
        _web.NavigationStarted += (_, args) =>
        {
            // Never downgrade a password-bearing session to HTTP, including an SSO redirect.
            if (args.Request?.Scheme is not "https" and not "about") { args.Cancel = true; Finish(new(WebSignInOutcome.Failed)); return; }
            _session.NavigationStarted();
        };
        _web.NavigationCompleted += async (_, args) =>
        {
            if (_finished) return;
            if (args.IsSuccess) await _session.PageLoadedAsync();
            else if (args.Request == _web.Source) Finish(new(WebSignInOutcome.Failed));
        };
        _web.WebResourceRequested += (_, args) => _session.RequestStarted(args.Request.Uri, args.Request.Method);
        _web.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.Request?.Scheme == "https") _web.Navigate(args.Request);
        };
        Closed += (_, _) =>
        {
            Dispatcher.UIThread.UnhandledException -= OnBrowserInitialisationError;
            _finished = true; _session.Dispose(); Content = null; _ = ClearProfileAsync();
        };
        _web.Source = request.Flow.TokensPage;
        Opened += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (!_finished && _web.TryGetPlatformHandle() is null) Finish(new(WebSignInOutcome.Failed));
        };
    }

    private void OnBrowserInitialisationError(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        // The native host initialises asynchronously. Contain failures from that component only.
        if (args.Exception.StackTrace?.Contains("Avalonia.Controls.NativeWebView", StringComparison.Ordinal) != true) return;
        args.Handled = true;
        Dispatcher.UIThread.Post(() => Finish(new(WebSignInOutcome.Failed)));
    }

    private async Task ClearProfileAsync()
    {
        // WebView2 may still hold its private profile briefly after its owner closes.
        if (Path.GetDirectoryName(_profile) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(_profile)) Directory.Delete(_profile, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
        }
    }

    private void Finish(WebSignInResult result)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Finish(result)); return; }
        if (_finished) return;
        _finished = true;
        Close(result);
    }
}
