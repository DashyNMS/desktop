using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>
/// Google Gemini as a Desktop Intelligence provider (#607), through the
/// Antigravity CLI (<c>agy</c>), Google's recommended terminal agent for
/// personal Google accounts and AI Pro and Ultra plans. Gemini CLI
/// (<c>gemini</c>) serves only enterprise and API-key accounts now, so it's
/// never picked up in its place.
/// </summary>
public sealed partial class GeminiAgentProvider : IAgentProvider
{
    private readonly string _workingDirectory;
    private readonly ILogger _logger;

    /// <param name="workingDirectory">An empty folder of DashyNMS's own for Antigravity CLI to run in, so it never sees a project.</param>
    public GeminiAgentProvider(string workingDirectory, ILogger logger)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
    }

    public string Id => AppSettings.IntelligenceProviderGemini;

    public string Name => "Gemini";

    public string Company => "Google";

    public AgentCapabilities Capabilities { get; } = new(WebSearch: false, Images: false, Usage: false);

    public async Task<AgentProviderStatus> CheckAsync(string? executablePath, CancellationToken cancellationToken)
    {
        var executable = AgentExecutables.Find(executablePath, "agy", Candidates());
        if (executable is null) return new AgentProviderStatus(AgentProviderState.NotInstalled);
        var versionLine = await AgentExecutables.VersionAsync(executable, cancellationToken).ConfigureAwait(false);
        if (versionLine is null)
        {
            return new AgentProviderStatus(AgentProviderState.NotInstalled, executable,
                Detail: "DashyNMS found Antigravity CLI but it didn't run. Reinstall it, or choose another executable below, then choose Check again.");
        }

        var version = Version().Match(versionLine) is { Success: true } m ? m.Value : versionLine;

        // agy has no sign-in status command; answering with it, and its sign-in check, arrive with #607.
        return new AgentProviderStatus(AgentProviderState.SignedOut, executable, version,
            Detail: "Antigravity CLI is installed. Sign in with your Google account by running agy in a terminal, then choose Check again.");
    }

    public Task<IReadOnlyList<AgentModel>> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<AgentModel>>(Array.Empty<AgentModel>());

    public Task<AgentUsage?> ReadUsageAsync(CancellationToken cancellationToken) => Task.FromResult<AgentUsage?>(null);

    public Task<AgentTurnResult> AskAsync(AgentTurn turn, AgentToolSession tools, IIntelligenceListener listener, CancellationToken cancellationToken)
        => throw new AgentProviderException("Gemini can't answer in this build yet.", IntelligenceFailure.Tools, AgentProviderState.Unavailable);

    public Task RetireAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Where Google's installers put agy, beyond PATH.</summary>
    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity CLI", "agy.exe");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin", "agy");
        }
    }

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex Version();
}
