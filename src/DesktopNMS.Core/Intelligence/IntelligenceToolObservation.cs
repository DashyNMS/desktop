using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>A display-safe execution receipt, without arguments, credentials or raw tool data.</summary>
public sealed record IntelligenceToolObservation(string Tool, DateTimeOffset ObservedAt, string Summary, bool IsError = false)
{
    public static IntelligenceToolObservation FromResult(string tool, IntelligenceToolResult result, DateTimeOffset observedAt)
    {
        if (result.IsError) return new(tool, observedAt, "Lookup failed. No successful result was returned.", true);
        try
        {
            if (result.Output.Length <= 1_000_000)
            {
                using var json = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 32 });
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var outcome)
                    && outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "error")
                    return new(tool, observedAt, "Lookup reported an error. No successful result was returned.", true);
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.GetString() == "ok"
                    && root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
                {
                    var text = new string((summary.GetString() ?? "").Where(c => !char.IsControl(c)).Take(350).ToArray());
                    if (!string.IsNullOrWhiteSpace(text)) return new(tool, observedAt, text);
                }
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return new(tool, observedAt, "Lookup completed.");
    }
}
