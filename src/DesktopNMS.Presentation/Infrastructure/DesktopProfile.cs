using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>Keep Windows upgrades on their existing profile while development builds stay isolated.</summary>
public static class DesktopProfile
{
    public static bool IsDevelopment(string version, bool debug) => debug || version.Contains("-dev.", StringComparison.OrdinalIgnoreCase);

    public static string ForBuild(string root, bool windows, bool development) => windows && !development
        ? Path.GetFullPath(root)
        : Path.Combine(Path.GetFullPath(root), "avalonia");

    /// <summary>WPF kept the main placement separately; other window keys already match.</summary>
    public static bool RestoreLegacyWindow(AppSettings settings)
    {
        if (settings.WindowPlacements.ContainsKey("MainWindow") || settings.Window is null) return false;
        settings.WindowPlacements["MainWindow"] = settings.Window.Clone();
        return true;
    }
}
