using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// Folders and files only this user can read, for what's handed to an agent
/// through the file system: an MCP configuration holding the bridge's token,
/// instructions holding the user's personality. Windows and macOS temporary
/// folders are per-user already; on Linux /tmp is shared, and the default
/// permissions would let another user read them.
/// </summary>
internal static class PrivateFiles
{
    private const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode File = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateFolder(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, Folder);
    }

    public static async Task WriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = File;
        await using var stream = new FileStream(path, options);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
