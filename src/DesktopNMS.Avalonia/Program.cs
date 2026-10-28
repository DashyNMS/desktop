using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using DesktopNMS.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DesktopNMS;

internal static class Program
{
    internal static StartupTiming Startup { get; } = new();

    internal static CrashReporter Reports { get; } = CreateReporter();

    internal static AppMetrics Metrics { get; } = new(Reports);

    internal static SessionHealth? Sessions { get; set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
#if WINDOWS
            try { Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.Uninstall(); } catch { }
#endif
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        var observingUi = false;
        try
        {
            BuildAvaloniaApp().AfterSetup(_ =>
            {
                Dispatcher.UIThread.UnhandledException += OnUiException;
                observingUi = true;
            }).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Reports.Capture(exception, CrashOrigin.Application);
            throw;
        }
        finally
        {
            Sessions?.Dispose();
            if (observingUi) Dispatcher.UIThread.UnhandledException -= OnUiException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            Metrics.Dispose();
            Reports.Dispose();
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            Reports.Capture(exception, CrashOrigin.Application);
    }

    private static void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs args)
        => Reports.Capture(args.Exception, CrashOrigin.UiThread);

    private static CrashReporter CreateReporter()
    {
        var assembly = typeof(Program).Assembly;
        var release = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var operatingSystem = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux" : "Unknown";
        var dsn = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "SentryDsn")?.Value ?? "";
        return new CrashReporter(new CrashReportContext(release, operatingSystem,
            RuntimeInformation.ProcessArchitecture.ToString()), new SentryCrashTransport(dsn));
    }

    /// <summary>Also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The brand font (#216) as every control's default.
            .With(new FontManagerOptions { DefaultFamilyName = "avares://DashyNMS/Assets/Fonts#IBM Plex Sans" })
            .LogToTrace();
}
