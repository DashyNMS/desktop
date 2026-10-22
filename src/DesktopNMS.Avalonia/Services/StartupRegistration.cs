using DesktopNMS.Security;
using Microsoft.Win32;

namespace DesktopNMS.Services;

/// <summary>The OS registration, rather than the saved Settings flag, is the source of truth.</summary>
public sealed class PlatformStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly IStartupRegistration? _native;

    public PlatformStartupRegistration()
    {
        if (OperatingSystem.IsMacOS())
            _native = new MacStartupRegistration(new ProcessCommandRunner(), Path.Combine(AppContext.BaseDirectory, "dashynms-login"));
        else if (OperatingSystem.IsLinux())
            _native = new XdgStartupRegistration(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                Environment.GetEnvironmentVariable("APPIMAGE") ?? Environment.ProcessPath ?? throw new InvalidOperationException("The app path couldn't be found."));
    }

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return _native?.IsEnabled == true;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("DashyNMS") is not null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("DashyNMS", $"\"{Environment.ProcessPath}\" --minimised", RegistryValueKind.String);
            else key.DeleteValue("DashyNMS", throwOnMissingValue: false);
        }
        else
        {
            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("FLATPAK_ID") is not null)
                throw new InvalidOperationException("This build supports login items in the native Linux package. Flatpak requires the background portal.");
            _native?.SetEnabled(enabled);
        }
    }
}
