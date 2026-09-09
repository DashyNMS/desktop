namespace DesktopNMS.Core.Configuration;

/// <summary>Where DashyNMS keeps its per-user state.</summary>
public static class AppPaths
{
    public const string FolderName = "DashyNMS";

    /// <summary>%APPDATA%\DashyNMS, created on first access.</summary>
    public static string DataDirectory { get; } = CreateDataDirectory();

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>DPAPI-encrypted API token.</summary>
    public static string TokenFile => Path.Combine(DataDirectory, "token.dat");

    /// <summary>Alert ids and states already notified about, so a restart is quiet.</summary>
    public static string NotificationStateFile => Path.Combine(DataDirectory, "notified.json");

    public static string LogDirectory { get; } = CreateSubdirectory("logs");

    private static string CreateDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(root, FolderName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSubdirectory(string name)
    {
        var path = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
