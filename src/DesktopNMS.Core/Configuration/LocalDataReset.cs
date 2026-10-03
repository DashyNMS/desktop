namespace DesktopNMS.Core.Configuration;

/// <summary>
/// What signing out removes (#231): everything DashyNMS keeps on this
/// computer - settings, the stored API token, the Unimus token and Graylog
/// password, dashboards, map layouts, custom maps, notification history and
/// cached map tiles - so the next start is the same as a fresh install.
/// Only the logs (for bug reports) and downloaded update installers stay.
/// </summary>
public static class LocalDataReset
{
    /// <summary>Kept: the diagnostic logs and any update installer already downloaded.</summary>
    public static IReadOnlySet<string> Kept { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "logs", "updates" };

    /// <summary>
    /// Deletes everything in <paramref name="dataDirectory"/> except
    /// <see cref="Kept"/>, and the whole of <paramref name="cacheDirectory"/>.
    /// Carries on past anything it can't delete and returns those paths, each
    /// with the reason, so one locked file doesn't leave the rest behind.
    /// </summary>
    public static IReadOnlyList<string> Wipe(string dataDirectory, string? cacheDirectory)
    {
        var failed = new List<string>();

        if (Directory.Exists(dataDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(dataDirectory))
            {
                TryDelete(() => File.Delete(file), file, failed);
            }

            foreach (var directory in Directory.EnumerateDirectories(dataDirectory))
            {
                if (!Kept.Contains(Path.GetFileName(directory)))
                {
                    TryDelete(() => Directory.Delete(directory, recursive: true), directory, failed);
                }
            }
        }

        if (!string.IsNullOrEmpty(cacheDirectory) && Directory.Exists(cacheDirectory))
        {
            TryDelete(() => Directory.Delete(cacheDirectory, recursive: true), cacheDirectory, failed);
        }

        return failed;
    }

    private static void TryDelete(Action delete, string path, List<string> failed)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(path + " (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
    }
}
