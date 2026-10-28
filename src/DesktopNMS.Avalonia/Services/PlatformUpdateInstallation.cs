using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopNMS.Services;

/// <summary>Use the installer owned by the running package; package managers retain control on Linux.</summary>
public sealed class PlatformUpdateInstallation : IUpdateInstallation
{
    private readonly MacUpdateInstallation? _mac = OperatingSystem.IsMacOS() ? new() : null;
    private readonly string? _appImage = Environment.GetEnvironmentVariable("APPIMAGE");
    public string? Runtime
    {
        get
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
            if (architecture is null) return null;
            if (OperatingSystem.IsWindows()) return architecture == "x64" ? "win-x64" : null;
            if (OperatingSystem.IsMacOS()) return _mac?.UnavailableReason is null ? "osx-" + architecture : null;
            if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"))
                && _appImage is not null && Path.IsPathFullyQualified(_appImage) && File.Exists(_appImage)) return "linux-" + architecture;
            return null;
        }
    }
    public string? UnavailableReason => _mac?.UnavailableReason;
    public bool RestartsApp => true;
    public string ActionText => OperatingSystem.IsMacOS() ? "Relaunch" : "Restart to update";

    public Task PrepareAsync(string verifiedPackage, string version, CancellationToken cancellationToken) =>
        _mac?.PrepareAsync(verifiedPackage, version, cancellationToken) ?? Task.CompletedTask;

    public async Task<bool> InstallAsync(string verifiedPackage)
    {
        if (OperatingSystem.IsWindows())
        {
            var app = Path.Combine(WindowsInstallDirectory(), "DashyNMS.exe");
            // Encode paths as PowerShell literals rather than interpolate them into cmd syntax.
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var command = "$ErrorActionPreference='Stop'; $installer = Start-Process -FilePath " + Quote(verifiedPackage)
                + " -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -WindowStyle Hidden -Wait -PassThru; Start-Process -FilePath " + Quote(app);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(argument);
            return Process.Start(start) is not null;
        }
        if (_mac is not null) return await _mac.InstallAsync(verifiedPackage).ConfigureAwait(false);
        if (OperatingSystem.IsLinux() && Runtime is not null && _appImage is not null)
        {
            return AppImageReplacement.Apply(verifiedPackage, _appImage, path =>
            {
                var start = new ProcessStartInfo(path) { UseShellExecute = false };
                start.ArgumentList.Add("--after"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Process.Start(start) is not null;
            });
        }
        return false;
    }

    private static string WindowsInstallDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B3D2C7A-4E1F-4A6B-8C5D-2F7A9E1B3C64}_is1");
            if (key?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location)) return location;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DashyNMS");
    }
}
