namespace DesktopNMS.Core.Configuration;

/// <summary>Where DashyNMS keeps its per-user state.</summary>
public static class AppPaths
{
    public const string FolderName = "DashyNMS";

    private static readonly object Sync = new();
    private static string? _dataDirectory;
    private static string? _logDirectory;

    /// <summary>%APPDATA%\DashyNMS (or the folder given to <see cref="UseDataDirectory"/>), created on first access.</summary>
    public static string DataDirectory
    {
        get
        {
            lock (Sync)
            {
                return _dataDirectory ??= CreateDataDirectory();
            }
        }
    }

    /// <summary>The usual data folder, whichever one is in use - e.g. for demo mode to keep its own inside it.</summary>
    public static string DefaultDataDirectory
    {
        get
        {
            // Create: off Windows the folder may not exist yet, and without it
            // GetFolderPath returns "" - leaving the data folder relative to the
            // working directory (#192).
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
            return Path.Combine(root, FolderName);
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>DPAPI-encrypted API token.</summary>
    public static string TokenFile => Path.Combine(DataDirectory, "token.dat");

    /// <summary>DPAPI-encrypted Unimus API token - kept separate from <see cref="TokenFile"/> since it is a distinct secret for a distinct service.</summary>
    public static string UnimusTokenFile => Path.Combine(DataDirectory, "unimus-token.dat");

    /// <summary>DPAPI-encrypted Graylog API password - its own file for the same reason as <see cref="UnimusTokenFile"/>.</summary>
    public static string GraylogPasswordFile => Path.Combine(DataDirectory, "graylog-password.dat");

    /// <summary>Alert ids and states already notified about, so a restart is quiet.</summary>
    public static string NotificationStateFile => Path.Combine(DataDirectory, "notified.json");

    public static string LogDirectory
    {
        get
        {
            lock (Sync)
            {
                return _logDirectory ??= CreateSubdirectory("logs");
            }
        }
    }

    /// <summary>Where a downloaded update's installer waits to be run - see Updates.UpdatePackage.</summary>
    public static string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    /// <summary>
    /// Keep everything in <paramref name="path"/> instead - demo mode's own
    /// folder, so the real settings, token and layouts are never touched.
    /// Only before anything has used the data folder.
    /// </summary>
    public static void UseDataDirectory(string path)
    {
        lock (Sync)
        {
            if (_dataDirectory is not null)
            {
                throw new InvalidOperationException("The data folder is already in use: " + _dataDirectory);
            }

            _dataDirectory = Path.GetFullPath(path);
            Directory.CreateDirectory(_dataDirectory);
        }
    }

    private static string CreateDataDirectory()
    {
        var path = DefaultDataDirectory;
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
