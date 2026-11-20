using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// GitHub Copilot as a Desktop Intelligence provider (#606), through the
/// GitHub Copilot CLI and whatever account it's signed in with.
/// </summary>
/// <remarks>
/// <para>
/// The CLI has no command that reports its sign-in without starting a chat,
/// so the check looks for what <c>copilot login</c> leaves behind - a token in
/// one of the environment variables it reads, or its entry in the system
/// credential store - and never reads the secret itself.
/// </para>
/// <para>
/// VS Code installs a <c>copilot</c> shim that only offers to install the CLI;
/// it comes first on PATH on many machines, so it's skipped in favour of the
/// CLI's own install.
/// </para>
/// </remarks>
public sealed partial class CopilotAgentProvider : IAgentProvider
{
    /// <summary>The variables the CLI takes a token from, in its order.</summary>
    internal static readonly string[] TokenVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    private readonly string _workingDirectory;
    private readonly ILogger _logger;

    /// <param name="workingDirectory">An empty folder of DashyNMS's own for Copilot to run in, so it never sees a project.</param>
    public CopilotAgentProvider(string workingDirectory, ILogger logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    public string Id => AppSettings.IntelligenceProviderCopilot;

    public string Name => "GitHub Copilot";

    public string Company => "GitHub";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: false, Images: false, Usage: false);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "copilot", Candidates().Where(c => !IsShim(c)))
            is { } found && (executablePath is not null || !IsShim(found)) ? found : FindOwnInstall();
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null || versionLine.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found a copilot command that isn't the GitHub Copilot CLI. Install the CLI, or choose its executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;
        if (!HasSignIn())
        {
            return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version);
        }

        return new AgentProviderStatus(AgentProviderState.Unavailable, executable, version,
            Detail: "GitHub Copilot is signed in. Answering with it arrives in a later development build.");
    }

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

    public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken) => Task.FromResult<AgentUsage?>(null);

    public Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
        => throw new AgentProviderException("GitHub Copilot can't answer in this build yet.", IntelligenceFailure.Tools, AgentProviderState.Unavailable);

    public Task RetireAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>VS Code's stand-in, which only offers to install the real CLI.</summary>
    internal static bool IsShim(string path) => path.Contains("copilot-shim", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the CLI's own installers put it.</summary>
    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubCopilotCLI", "copilot.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "copilot.cmd");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "copilot");
            yield return "/opt/homebrew/bin/copilot";
            yield return "/usr/local/bin/copilot";
        }
    }

    private static string? FindOwnInstall() => Candidates().FirstOrDefault(c => !IsShim(c) && File.Exists(c));

    /// <summary>
    /// Whether <c>copilot login</c> has left a sign-in: a token variable, or an
    /// entry in the credential store. Only the entry's existence is checked.
    /// </summary>
    private static bool HasSignIn()
    {
        if (TokenVariables.Any(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))) return true;
        return OperatingSystem.IsWindows() ? WindowsCredentials.Any("copilot*") : true;
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Version();

    /// <summary>The Windows credential store, asked only whether an entry exists.</summary>
    private static class WindowsCredentials
    {
        public static bool Any(string filter)
        {
            if (!CredEnumerate(filter, 0, out var count, out var credentials)) return false;
            CredFree(credentials);
            return count > 0;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
