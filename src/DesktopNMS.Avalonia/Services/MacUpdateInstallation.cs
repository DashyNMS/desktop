using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopNMS.Services;

/// <summary>Stages a complete bundle before offering Relaunch; a native helper survives app shutdown.</summary>
internal sealed class MacUpdateInstallation
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Prepared? _prepared;
    private readonly string _app;
    private readonly string _architecture;

    public MacUpdateInstallation()
    {
        _app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        _architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
    }

    public string? UnavailableReason
    {
        get
        {
            var parent = Path.GetDirectoryName(_app)!;
            var userApplications = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            if (!Directory.Exists(_app) || !string.Equals(Path.GetExtension(_app), ".app", StringComparison.Ordinal)
                || parent != "/Applications" && parent != userApplications)
                return "Install DashyNMS in your user Applications folder (~/Applications), or a writable /Applications folder, before updating. Apps running from a disk image or another location cannot update in place.";
            if (NativeAccess(_app, 2) != 0 || NativeAccess(parent, 2) != 0)
                return "DashyNMS cannot replace this installation without administrator access. Move it to ~/Applications or ask your administrator to install the update. The current app has been kept.";
            return null;
        }
    }

    public async Task PrepareAsync(string package, string version, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prepared is { } ready && ready.Package == package && ready.Version == version
                && Directory.Exists(ready.Directory)) return;
            if (_prepared is { } previous) DeleteUnapplied(previous.Directory);
            _prepared = null;
            var start = new ProcessStartInfo(Path.Combine(_app, "Contents", "MacOS", "dashynms-update"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // A detached native helper must not inherit the host's LaunchServices identity.
            start.Environment.Remove("__CFBundleIdentifier");
            foreach (var argument in new[] { "prepare", _app, package, version.TrimStart('v'), _architecture }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("The macOS updater could not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            // Let the helper finish cleanup (including unmounting) even when the
            // channel or preference changes. Cancellation prevents publication.
            await process.WaitForExitAsync().ConfigureAwait(false);
            var directory = (await output.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0) throw new IOException((await error.ConfigureAwait(false)).Trim());
            if (!IsTransactionDirectory(_app, directory)) throw new IOException("The macOS updater returned an invalid preparation folder.");
            if (cancellationToken.IsCancellationRequested)
            {
                DeleteUnapplied(directory);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _prepared = new(package, version, directory);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> InstallAsync(string package)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_prepared is not { } ready || ready.Package != package) return false;
            // A previous helper may have exited while waiting for shutdown.
            // Its receipt cannot authorise a new attempt.
            File.Delete(Path.Combine(ready.Directory, "handoff"));
            var start = new ProcessStartInfo(Path.Combine(ready.Directory, "apply"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.Environment.Remove("__CFBundleIdentifier");
            foreach (var argument in new[] { "apply", ready.Directory, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new IOException("The macOS updater could not start.");
            // The helper writes handoff only after revalidation and taking the
            // destination lock. Never close the host merely because it spawned.
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var deadline = Stopwatch.StartNew();
            while (!process.HasExited && deadline.Elapsed < TimeSpan.FromMinutes(3))
            {
                if (File.Exists(Path.Combine(ready.Directory, "handoff")))
                {
                    _prepared = null;
                    // Keep redirected streams draining while this process lives.
                    _ = ObserveExitAsync(process, error);
                    return true;
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            process.Dispose();
            throw new IOException((await error.ConfigureAwait(false)).Trim() is { Length: > 0 } message
                ? message : "The macOS updater could not prepare a safe handoff. The installed app has been kept.");
        }
        finally { _gate.Release(); }
    }

    private static async Task ObserveExitAsync(Process process, Task<string> error)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); await error.ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    internal static bool IsTransactionDirectory(string app, string directory) => Path.IsPathFullyQualified(directory)
        && Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.GetDirectoryName(Path.GetFullPath(app))
        && Path.GetFileName(directory).StartsWith(".DashyNMS-update-", StringComparison.Ordinal)
        && Guid.TryParse(Path.GetFileName(directory)[".DashyNMS-update-".Length..], out _)
        && Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is null;

    private static void DeleteUnapplied(string directory)
    {
        // A transaction with a handoff or state file may contain the only prior
        // working bundle. Only disposable, never-applied staging can be removed.
        if (!File.Exists(Path.Combine(directory, "state.json")) && !File.Exists(Path.Combine(directory, "handoff")))
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Ordinary launches cannot race the helper between exit and replacement.</summary>
    public static bool CanStartDuringUpdate()
    {
        if (!OperatingSystem.IsMacOS()) return true;
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var directory = Environment.GetEnvironmentVariable("DASHYNMS_UPDATE_DIRECTORY");
        var token = Environment.GetEnvironmentVariable("DASHYNMS_UPDATE_TOKEN");
        if (directory is not null && token is not null && IsTransactionDirectory(app, directory))
        {
            try
            {
                using var transaction = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "transaction.json")));
                if (transaction.RootElement.GetProperty("app").GetString() == app
                    && transaction.RootElement.GetProperty("token").GetString() == token) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
        }
        var path = Path.Combine(Path.GetDirectoryName(app)!, ".DashyNMS-update-lock");
        // Darwin O_NOFOLLOW; do not create a lock for ordinary app launches.
        var descriptor = NativeOpen(path, 0x100);
        if (descriptor < 0) return !File.Exists(path);
        try { return NativeFlock(descriptor, 1 | 4) == 0; } // LOCK_SH | LOCK_NB
        finally { NativeClose(descriptor); }
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "access", SetLastError = true)]
    private static extern int NativeAccess([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int NativeFlock(int descriptor, int operation);
    [DllImport("libSystem.B.dylib", EntryPoint = "close")]
    private static extern int NativeClose(int descriptor);

    /// <summary>Confirm managed/UI startup without waiting for network sign-in or starting MCP unnecessarily.</summary>
    public static void ConfirmStartup()
    {
        var directory = Environment.GetEnvironmentVariable("DASHYNMS_UPDATE_DIRECTORY");
        var token = Environment.GetEnvironmentVariable("DASHYNMS_UPDATE_TOKEN");
        Environment.SetEnvironmentVariable("DASHYNMS_UPDATE_DIRECTORY", null);
        Environment.SetEnvironmentVariable("DASHYNMS_UPDATE_TOKEN", null);
        if (!OperatingSystem.IsMacOS() || directory is null || token is null) return;
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        TryConfirmStartup(app, directory, token, DesktopUpdateService.RunningVersion(), Environment.ProcessId);
    }

    internal static bool TryConfirmStartup(string app, string directory, string token, string version, int pid)
    {
        try
        {
            if (!IsTransactionDirectory(app, directory)) return false;
            using var transaction = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "transaction.json")));
            var value = transaction.RootElement;
            if (value.GetProperty("token").GetString() != token || value.GetProperty("app").GetString() != app
                || value.GetProperty("version").GetString() != version) return false;
            // The native helper checks the sealed bundle and matching native MCP
            // hash before swapping. This receipt additionally proves this exact
            // release claimed the instance and reached the UI dispatcher.
            var temporary = Path.Combine(directory, "startup-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { token, version, pid }));
            File.Move(temporary, Path.Combine(directory, "startup.json"), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }

    /// <summary>Explain an interrupted or rolled-back update once, on the next normal launch.</summary>
    public static string? ReadRecoveryNotice()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var parent = Path.GetDirectoryName(app)!;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, ".DashyNMS-update-*").OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (!IsTransactionDirectory(app, directory) || File.Exists(Path.Combine(directory, "failure-shown"))) continue;
                var failure = Path.Combine(directory, "failure.json");
                if (!File.Exists(failure)) continue;
                using var transaction = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "transaction.json")));
                if (transaction.RootElement.GetProperty("app").GetString() != app) continue;
                using var report = JsonDocument.Parse(File.ReadAllText(failure));
                var message = report.RootElement.GetProperty("message").GetString();
                using var marker = new FileStream(Path.Combine(directory, "failure-shown"), FileMode.CreateNew, FileAccess.Write);
                return message;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
        return null;
    }

    private sealed record Prepared(string Package, string Version, string Directory);
}
