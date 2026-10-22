namespace DesktopNMS.Infrastructure;

/// <summary>Words that differ by OS, for the views (#250) - the WPF app's text says "your Windows account".</summary>
public static class PlatformText
{
    /// <summary>"…stored encrypted for {this}" - where #154 keeps secrets.</summary>
    public static string SecretsKeptFor { get; } =
        OperatingSystem.IsWindows() ? "your Windows account only"
        : OperatingSystem.IsMacOS() ? "you only, with a key in your Keychain"
        : "you only, with a key in your keyring";

    /// <summary>Where the tray icon lives (#252): "the notification area", "the menu bar", "the system tray".</summary>
    public static string TrayPlace { get; } =
        OperatingSystem.IsWindows() ? "the notification area"
        : OperatingSystem.IsMacOS() ? "the menu bar"
        : "the system tray";

    /// <summary>
    /// Whether DashyNMS can live on in the tray with no window open (#252).
    /// Not on Linux: many desktops (GNOME without an extension) show no tray at
    /// all, and there'd be no way back to an app hidden there - so closing the
    /// window there always quits.
    /// </summary>
    public static bool CanHideToTray { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>"this PC", "this Mac", "this computer".</summary>
    public static string ThisComputer { get; } =
        OperatingSystem.IsWindows() ? "this PC"
        : OperatingSystem.IsMacOS() ? "this Mac"
        : "this computer";
}
