using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Sensors;

namespace DesktopNMS.Services;

public sealed record ComponentVersion(string Name, string Version, string State, string Details = "")
{
    public bool HasDetails => Details.Length > 0;
}

/// <summary>Offline package identity. Never executes the tools or reads account configuration.</summary>
public static class ComponentVersions
{
    public const string MetadataFile = "version.json";
    public const string Unavailable = "Unavailable";

    public static string AssemblyVersion(Assembly assembly) =>
        Clean(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? assembly.GetName().Version?.ToString() ?? Unavailable);

    // Metadata is deliberately constrained: support text must never copy arbitrary file contents or paths.
    internal static string Clean(string? value) => value is { Length: > 0 and <= 200 }
        && Regex.IsMatch(value, @"\A[0-9A-Za-z][0-9A-Za-z.+_-]*\z", RegexOptions.CultureInvariant)
        ? value : Unavailable;

    public static ComponentVersion Sensor(SensorDefinitionStore? store)
    {
        if (store is null) return new("Sensor definitions", Unavailable, "Unavailable in this build");
        // Core exposes human-readable status rather than an error enum. Map only its known
        // failure markers; never include the raw exception (which can contain a source URL).
        var failed = store.Status.StartsWith("Couldn't", StringComparison.Ordinal)
            || store.Status.Contains("; update unavailable,", StringComparison.Ordinal);
        var state = failed ? store.Version is null ? "Unavailable" : "Using cached catalogue; update unavailable"
            : !store.Options.Enabled ? "Disabled" : store.Version is null ? "Not downloaded" : "Enabled";
        return new("Sensor definitions", Clean(store.Version), state,
            store.LastSuccessfulCheck is { } last ? $"Last successful check: {last:u}" : "Not checked");
    }

    internal static string MetadataDirectory(string executable, bool? macOS = null) => (macOS ?? OperatingSystem.IsMacOS())
        && Directory.GetParent(Path.GetDirectoryName(executable)!) is { Name: "MacOS", Parent: { } contents }
        ? Path.Combine(contents.FullName, "Resources", "mcp") : Path.GetDirectoryName(executable)!;

    public static async Task<ComponentVersion> ReadMcpAsync(string executable)
    {
        if (!File.Exists(executable)) return new("MCP server", Unavailable, "Missing");
        var directory = MetadataDirectory(executable);
        try
        {
            var path = Path.Combine(directory, MetadataFile);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 4096) return InvalidMetadata();
                var metadata = JsonSerializer.Deserialize<McpBuild>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
                if (metadata is null || metadata.SchemaVersion != 1 || Clean(metadata.Version) == Unavailable
                    || !Regex.IsMatch(metadata.Commit ?? "", @"\A[0-9a-fA-F]{40}\z")
                    || !Regex.IsMatch(metadata.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z")
                    || Clean(metadata.Runtime) == Unavailable) return InvalidMetadata();
                await using var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                if (!hash.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new("MCP server", Unavailable, "Bundle metadata does not match executable");
                return new("MCP server", metadata.Version!, "Bundled", $"Commit: {metadata.Commit}; runtime: {metadata.Runtime}");
            }
            var legacy = Path.Combine(directory, "BUILD.txt");
            if (File.Exists(legacy) && new FileInfo(legacy).Length <= 1024)
            {
                var match = Regex.Match(await File.ReadAllTextAsync(legacy).ConfigureAwait(false),
                    @"\ADashyNMS MCP ([0-9a-fA-F]{40}) \(([a-z0-9-]+)\)\s*\z");
                if (match.Success) return new("MCP server", Unavailable, "Bundled; version not recorded",
                    $"Unverified bundle provenance: {match.Groups[1].Value} ({match.Groups[2].Value})");
            }
            return new("MCP server", Unavailable, "Bundled; version not recorded");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return InvalidMetadata();
        }
    }

    private static ComponentVersion InvalidMetadata() => new("MCP server", Unavailable, "Bundle metadata unavailable");

    public static string Format(IEnumerable<ComponentVersion> components) => string.Join("\n", components.Select(c =>
        $"{c.Name}: {c.Version} ({c.State})" + (c.Details.Length == 0 ? "" : $"\n  {c.Details}")))
        + $"\nOS: {RuntimeInformation.OSDescription.Replace('\r', ' ').Replace('\n', ' ')}\nArchitecture: {RuntimeInformation.ProcessArchitecture}";

    internal static Uri AddToBugReport(Uri template, IEnumerable<ComponentVersion> components)
    {
        var query = template.Query.TrimStart('?').Split('&').Select(part =>
        {
            if (!part.StartsWith("body=", StringComparison.Ordinal)) return part;
            var body = Uri.UnescapeDataString(part[5..]).Replace("\nWindows: ", "\nOS: ", StringComparison.Ordinal);
            return "body=" + Uri.EscapeDataString(body + "\n\nComponent versions:\n" + Format(components));
        });
        return new UriBuilder(template) { Query = string.Join("&", query) }.Uri;
    }

    internal sealed record McpBuild(int SchemaVersion, string? Version, string? Commit, string? Runtime, string? Sha256);
}
