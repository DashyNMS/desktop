using Avalonia;
using Avalonia.Media;

namespace DesktopNMS;

internal static class Program
{
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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The brand font (#216) as every control's default.
            .With(new FontManagerOptions { DefaultFamilyName = "avares://DashyNMS/Assets/Fonts#IBM Plex Sans" })
            .LogToTrace();
}
