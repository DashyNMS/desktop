namespace DesktopNMS.Services;

/// <summary>Replace a user-owned AppImage on the same filesystem and restore it if restart fails.</summary>
public static class AppImageReplacement
{
    public static bool Apply(string verifiedPackage, string currentImage, Func<string, bool> restart)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var target = Path.GetFullPath(currentImage);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Update the AppImage file itself rather than a symbolic link.");
        var directory = Path.GetDirectoryName(target)!;
        var prefix = Path.Combine(directory, ".dashynms-update-" + Guid.NewGuid().ToString("N"));
        var stage = prefix + ".AppImage";
        var backup = prefix + ".previous";
        var replaced = false;
        try
        {
            File.Copy(verifiedPackage, stage);
            var permissions = File.GetUnixFileMode(target) & (UnixFileMode)0x1FF;
            File.SetUnixFileMode(stage, permissions | UnixFileMode.UserExecute);
            File.Copy(target, backup);
            File.Move(stage, target, overwrite: true);
            replaced = true;
            if (!restart(target)) throw new IOException("Could not restart the updated AppImage.");
            try { File.Delete(backup); } catch (IOException) { /* The running update is already launched; retain the backup. */ }
            return true;
        }
        catch
        {
            if (replaced) File.Move(backup, target, overwrite: true);
            throw;
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
            if (!replaced && File.Exists(backup)) File.Delete(backup);
        }
    }
}
