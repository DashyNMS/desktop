using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DesktopNMS.WebSignInSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => AppBuilder.Configure<SmokeApplication>()
        .UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

/// <summary>Exercise the real native engine with local synthetic HTML, without sign-ins or network requests.</summary>
public sealed class SmokeApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var browser = new NativeWebView();
        if (OperatingSystem.IsWindows()) browser.AdapterPreference = [WebViewAdapterType.WebView2];
        else if (OperatingSystem.IsLinux()) browser.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        browser.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            switch (args)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs windows:
                    windows.IsInPrivateModeEnabled = true;
                    windows.UserDataFolder = Path.Combine(Path.GetTempPath(), "DashyNMS-Smoke-" + Guid.NewGuid().ToString("N"));
                    break;
                case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                    apple.NonPersistentDataStore = true; break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.EphemeralDataManager = true; break;
            }
        };
        var window = new Window
        {
            Width = 480, Height = 300, ShowInTaskbar = false, Content = browser,
            Title = "DashyNMS sign-in browser check",
        };
        browser.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess) { lifetime.Shutdown(1); return; }
            try
            {
                var answer = await browser.InvokeScript("JSON.stringify({nativeBrowser:true,scriptResult:6*7})");
                Console.WriteLine(answer);
                lifetime.Shutdown(answer?.Contains("42", StringComparison.Ordinal) == true ? 0 : 1);
            }
            catch { lifetime.Shutdown(2); }
        };
        window.Opened += (_, _) => browser.NavigateToString("<html><body>Checking the sign-in browser</body></html>");
        lifetime.MainWindow = window;
        DispatcherTimer.RunOnce(() => lifetime.Shutdown(3), TimeSpan.FromSeconds(30));
        base.OnFrameworkInitializationCompleted();
    }
}
