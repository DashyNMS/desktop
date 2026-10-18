using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>A network tool the model may call: its name, what it does, and its arguments' JSON schema.</summary>
public sealed record IntelligenceTool(string Name, string Description, JsonElement InputSchema);

/// <summary>What a tool call returned: text for the model (already bounded), whether it was an error, and any images (a graph).</summary>
public sealed record IntelligenceToolResult(string Output, bool IsError, IReadOnlyList<IntelligenceImage>? Images = null);

/// <summary>An image a tool returned - a LibreNMS graph, as PNG.</summary>
public sealed record IntelligenceImage(byte[] Data, string MimeType)
{
    /// <summary>Larger than this isn't shown or sent.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public string DataUrl => "data:" + MimeType + ";base64," + Convert.ToBase64String(Data);
}

/// <summary>
/// Where the network tools come from. On desktop, the bundled DashyNMS MCP
/// server over stdio - the MCP repository stays the one source of what the
/// tools do. Everything here is read-only.
/// </summary>
public interface IIntelligenceTools
{
    /// <summary>Starts the tools if they aren't running, and lists the approved ones.</summary>
    Task<IReadOnlyList<IntelligenceTool>> GetToolsAsync(CancellationToken cancellationToken);

    Task<IntelligenceToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// The tools allowed in the first release: every DashyNMS MCP tool reads
/// only, but each one is still named here after review rather than trusting
/// whatever the server lists. <c>graph</c> returns a PNG alongside its text:
/// shown in the conversation, and sent to the model as an image.
/// </summary>
public static class IntelligenceToolAllowlist
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "active_alerts",
        "alert_details",
        "alert_rules",
        "config_backup",
        "config_diff",
        "device_details",
        "device_events",
        "device_health",
        "device_neighbours",
        "device_sensors",
        "find_device",
        "graph",
        "health_overview",
        "list_groups_and_locations",
        "network_events",
        "network_overview",
        "port_status",
        "recent_config_changes",
        "search_logs",
        "top_devices",
        "top_errors",
        "top_interfaces",
        "trace_endpoint",
        "what_changed",
    };
}
