using DesktopNMS.Security;

namespace DesktopNMS.Services;

/// <summary>Native Linux packages use the user's XDG autostart directory.</summary>
public sealed class XdgStartupRegistration(string configDirectory, string executable) : IStartupRegistration
{
    private string FilePath => Path.Combine(configDirectory, "autostart", "net.dashynms.desktop.desktop");

    public bool IsEnabled
    {
        get
        {
            if (!File.Exists(FilePath)) return false;
            var lines = File.ReadAllLines(FilePath);
            return lines.Contains("Exec=" + Command)
                && !lines.Any(line => line.Trim().Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)
                    || line.Trim().Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase));
        }
    }

    private string Command => QuoteExecutable(executable) + " --minimised";

    public void SetEnabled(bool enabled)
    {
        if (!enabled) { File.Delete(FilePath); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "[Desktop Entry]\nType=Application\nName=DashyNMS\nExec=" + Command
            + "\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
    }

    /// <summary>Desktop Entry quoting, including field-code escaping; this is not a shell command.</summary>
    internal static string QuoteExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("The app path isn't valid for an autostart entry.", nameof(path));
        // Exec arguments are unescaped twice: once by the Desktop Entry parser,
        // then by its quoted-argument parser. A literal backslash needs four.
        var quoted = path.Replace("\\", "\\\\\\\\").Replace("\"", "\\\\\"")
            .Replace("$", "\\\\$").Replace("`", "\\\\`").Replace("%", "%%");
        return "\"" + quoted + "\"";
    }
}

/// <summary>macOS 13+ login items, registered by the helper inside the signed app bundle.</summary>
public sealed class MacStartupRegistration(ICommandRunner runner, string helperPath) : IStartupRegistration
{
    public bool IsEnabled => runner.Run(helperPath, ["status"]) is { ExitCode: 0, Output: var status } && status.Trim() == "enabled";

    public void SetEnabled(bool enabled)
    {
        var result = runner.Run(helperPath, [enabled ? "enable" : "disable"]);
        if (!result.Started)
            throw new InvalidOperationException("Install the macOS app bundle to change login items.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Error.Trim() is { Length: > 0 } reason ? reason : "The login item couldn't be changed.");
    }
}
