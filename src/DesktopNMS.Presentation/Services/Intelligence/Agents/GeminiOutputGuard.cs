using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopNMS.Services.Intelligence.Agents;

/// <summary>Checks the file path before Antigravity reads it and again when its stream reports the read.</summary>
internal static class GeminiOutputGuard
{
    internal const string HookArgument = "--check-gemini-output-read";

    internal static bool IsSupportedVersion(string version)
        => Version.TryParse(version, out var cliVersion) && cliVersion >= new Version(1, 5, 17);

    internal static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

    /// <summary>
    /// Workspace-only PreToolUse hook. Antigravity implicitly allows reads from the conversation's
    /// artifact folder, which contains more than MCP output. The hook denies those reads before they happen.
    /// </summary>
    internal static JsonObject? CreateHook(string? executable)
    {
        // The hook launches the Desktop app host, which handles the flag before starting Avalonia.
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileNameWithoutExtension(executable), "DashyNMS", StringComparison.OrdinalIgnoreCase)) return null;

        string command;
        if (OperatingSystem.IsWindows())
        {
            // Antigravity passes hook commands through cmd.exe. Avoid environment expansion inside the path.
            if (executable.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0) return null;
            command = $"\"{executable}\" {HookArgument}";
        }
        else
        {
            command = "'" + executable.Replace("'", "'\"'\"'") + "' " + HookArgument;
        }

        return new JsonObject
        {
            ["dashynms-output-read"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "read_file|view_file|list_dir|find_by_name|grep_search|sed_file",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                }),
            },
        };
    }

    internal static string HookDecision(string input, string geminiHome)
    {
        const string deny = """{"decision":"deny","reason":"Only generated MCP output can be read in DashyNMS."}""";
        try
        {
            if (JsonNode.Parse(input) is not JsonObject request || request["toolCall"] is not JsonObject tool ||
                tool["name"]?.ToString() != "read_file") return deny;

            var conversation = request["conversationId"]?.ToString();
            return AllowsRead(geminiHome, conversation, tool["args"] as JsonObject) ? """{"decision":"allow"}""" : deny;
        }
        catch (JsonException)
        {
            return deny;
        }
    }

    /// <summary>Only numbered tool-output files from the active conversation are eligible for a read.</summary>
    internal static bool AllowsRead(string geminiHome, string? conversation, JsonObject? parameters)
    {
        if (!Guid.TryParseExact(conversation, "D", out _) ||
            parameters?["Path"] is not JsonValue value ||
            !value.TryGetValue<string>(out var path) || !Path.IsPathFullyQualified(path)) return false;

        var root = geminiHome;
        var directories = new[] { "antigravity-cli", "brain", conversation!, ".system_generated", "steps" };
        var steps = Path.Combine(root, Path.Combine(directories));
        var prefix = steps + Path.DirectorySeparatorChar;
        path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // Windows drive letters are case-insensitive even on a volume with case-sensitive directories.
        if (OperatingSystem.IsWindows() && path.Length > 1 && path[1] == ':' && prefix[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path[1..];
            prefix = char.ToUpperInvariant(prefix[0]) + prefix[1..];
        }
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var parts = path[prefix.Length..].Split(Path.DirectorySeparatorChar);
        if (parts.Length != 2 || parts[1] != "output.txt" ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)) return false;

        try
        {
            // Reject links and junctions at each level before inspecting anything inside them.
            const FileAttributes directory = FileAttributes.Directory | FileAttributes.ReparsePoint;
            if ((File.GetAttributes(root) & directory) != FileAttributes.Directory) return false;
            foreach (var segment in directories.Append(parts[0]))
            {
                root = Path.Combine(root, segment);
                if ((File.GetAttributes(root) & directory) != FileAttributes.Directory) return false;
            }

            return (File.GetAttributes(path) & directory) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
