using DesktopNMS.Core.Sensors;

namespace DesktopNMS.Services;

/// <summary>
/// Where desktop keeps the sensor definitions preference and its downloaded definitions.
/// Core's <see cref="SensorDefinitionPaths.ForInstallation"/> puts them beside the app,
/// which on macOS left a stray file in /Applications, so desktop keeps them in the data
/// folder with everything else and moves an older preference across once.
/// </summary>
public static class SensorDefinitionLocation
{
    public const string SettingsFileName = "sensor-definitions.json";
    public const string DefinitionsDirectoryName = "sensor-definitions";

    // Core refuses a larger preference file, so there's nothing to keep beyond this.
    private const int MaximumSettingsBytes = 8192;

    /// <summary>Both entries sit directly in the data folder, as the store requires a shared parent.</summary>
    public static SensorDefinitionPaths InDataDirectory(string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory);
        return new SensorDefinitionPaths(Path.Combine(root, SettingsFileName), Path.Combine(root, DefinitionsDirectoryName));
    }

    /// <summary>
    /// Copies the preference from the old installation location when there isn't one yet,
    /// then tries to remove the old preference and definitions. Best effort throughout: a
    /// read-only installation folder leaves the old files in place without stopping startup,
    /// and the definitions are downloaded again rather than copied.
    /// </summary>
    /// <param name="current">The data folder location in use.</param>
    /// <param name="legacy">Works out the old location; a failure there just skips the move.</param>
    /// <returns>Whether a preference was copied across.</returns>
    public static bool MigrateLegacy(SensorDefinitionPaths current, Func<SensorDefinitionPaths> legacy)
    {
        SensorDefinitionPaths old;
        try
        {
            old = legacy();
            old = new SensorDefinitionPaths(Path.GetFullPath(old.SettingsFile), Path.GetFullPath(old.DefinitionsDirectory));
        }
        catch (Exception)
        {
            // The old location depends on the install path and APPIMAGE; neither may
            // stop the app from starting.
            return false;
        }

        if (SamePath(old.SettingsFile, current.SettingsFile) || SamePath(old.DefinitionsDirectory, current.DefinitionsDirectory))
        {
            return false;
        }

        var copied = false;
        if (!File.Exists(current.SettingsFile) && File.Exists(old.SettingsFile))
        {
            var bytes = TryRead(old.SettingsFile);
            if (bytes is null || !TryWriteNew(current.SettingsFile, bytes))
            {
                // Keep the old preference so a later start can try again.
                return false;
            }

            copied = true;
        }

        // Only remove the old preference once the new one is safely in place.
        if (File.Exists(current.SettingsFile))
        {
            TryDelete(() => File.Delete(old.SettingsFile));
        }

        TryDelete(() => DeleteDirectory(old.DefinitionsDirectory));
        return copied;
    }

    /// <summary>
    /// Runs the sign-out reset while keeping the preference, which belongs to the
    /// installation rather than the account (downloaded definitions go and come back).
    /// </summary>
    public static void ResetKeepingPreference(SensorDefinitionPaths paths, Action reset)
    {
        var kept = File.Exists(paths.SettingsFile) ? TryRead(paths.SettingsFile) : null;
        reset();
        if (kept is not null && !File.Exists(paths.SettingsFile))
        {
            TryWriteNew(paths.SettingsFile, kept);
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b,
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static byte[]? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[MaximumSettingsBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return total > MaximumSettingsBytes ? null : buffer[..total];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes through a temporary file and never replaces a preference saved meanwhile.</summary>
    private static bool TryWriteNew(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(() => File.Delete(temporary));
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // A linked folder: remove the link itself, never what it points at.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only installation folders (such as /Applications for a standard
            // user) keep the old files; they're no longer read.
        }
    }
}
