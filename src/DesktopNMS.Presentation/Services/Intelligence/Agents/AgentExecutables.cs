using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// Finding an agent's executable (#332): the one the user chose, else the
/// first found on PATH or in the folders its installers use. A desktop app's
/// PATH is often shorter than a terminal's - on macOS especially - so the
/// usual folders are searched too. Nothing is installed or changed.
/// </summary>
public static class AgentExecutables
{
    /// <summary>How long <c>--version</c> may take before the agent counts as not answering.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The executable to run: <paramref name="chosen"/> when set (whether or not
    /// it exists - the check then says so), else the first candidate found.
    /// </summary>
    public static string? Find(string? chosen, string name, IEnumerable<string>? extraCandidates = null)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) return Environment.ExpandEnvironmentVariables(chosen.Trim());
        foreach (var candidate in Candidates(name).Concat(extraCandidates ?? Array.Empty<string>()))
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>The names to look for in each folder: a native executable before a script launcher.</summary>
    private static IEnumerable<string> FileNames(string name) => OperatingSystem.IsWindows()
        ? [name + ".exe", name + ".cmd"]
        : [name];

    private static IEnumerable<string> Candidates(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var folders = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(CommonFolders());
        foreach (var file in FileNames(name))
        {
            foreach (var folder in folders)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim('"'), file);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return candidate;
            }
        }
    }

    /// <summary>Where agent installers usually put their launchers, beyond PATH.</summary>
    internal static IEnumerable<string> CommonFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new[]
            {
                Path.Combine(home, ".local", "bin"),
                Path.Combine(appData, "npm"),
                Path.Combine(home, "scoop", "shims"),
                Path.Combine(home, ".bun", "bin"),
            };
        }

        return new[]
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, ".bun", "bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
        };
    }

    /// <summary>
    /// Runs <c>--version</c> and returns its first line, or null when it doesn't
    /// run or answer. Proves the file is an executable that works here before
    /// anything is asked of it.
    /// </summary>
    public static async Task<string?> VersionAsync(string executable, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) return null;
        AgentProcess process;
        try
        {
            process = AgentProcess.Start(executable, ["--version"], Path.GetTempPath(), redirectInput: false);
        }
        catch (AgentProviderException)
        {
            return null;
        }

        await using (process.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(VersionTimeout);
            try
            {
                var line = await process.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                var code = await process.Exited.WaitAsync(timeout.Token).ConfigureAwait(false);
                return code == 0 && !string.IsNullOrWhiteSpace(line) ? line.Trim() : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
