using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DesktopNMS.Core.Models;

/// <summary>
/// Renders a LibreNMS dynamic device group's <see cref="DeviceGroup.Rules"/>
/// (a jQuery QueryBuilder JSON tree) as a compact, SQL-like condition string
/// for display - e.g. "devices.sysName LIKE 'w-%' OR devices.display LIKE
/// 'w-%'". Read-only/best-effort: this app has no editor for rules (see
/// <see cref="DeviceGroup.IsEditableAsStatic"/>), so this only ever needs to
/// be legible to a human, never round-trippable back into the original JSON
/// or exhaustively correct for every jQuery QueryBuilder operator LibreNMS
/// could theoretically produce.
/// </summary>
public static class DeviceGroupRuleFormatter
{
    public static string? Format(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rulesJson);
            return FormatGroup(document.RootElement, isRoot: true);
        }
        catch (JsonException)
        {
            // Not a rules JSON tree (e.g. this app has not seen this shape,
            // or Rules fell back to raw non-JSON text) - show nothing rather
            // than garbled fallback text.
            return null;
        }
    }

    private static string? FormatGroup(JsonElement group, bool isRoot)
    {
        if (group.ValueKind != JsonValueKind.Object
            || !group.TryGetProperty("condition", out var conditionElement)
            || !group.TryGetProperty("rules", out var rulesElement)
            || rulesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var condition = conditionElement.GetString()?.ToUpperInvariant() == "OR" ? "OR" : "AND";

        var parts = new List<string>();
        foreach (var rule in rulesElement.EnumerateArray())
        {
            var part = rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty("condition", out _)
                ? FormatGroup(rule, isRoot: false)
                : FormatCondition(rule);

            if (!string.IsNullOrEmpty(part))
            {
                parts.Add(part!);
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var joined = string.Join($" {condition} ", parts);
        return isRoot || parts.Count == 1 ? joined : $"({joined})";
    }

    private static string? FormatCondition(JsonElement rule)
    {
        if (!rule.TryGetProperty("field", out var fieldElement) || fieldElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var field = fieldElement.GetString();
        var op = rule.TryGetProperty("operator", out var opElement) ? opElement.GetString() : null;
        rule.TryGetProperty("value", out var valueElement);

        return op switch
        {
            "equal" => $"{field} = '{FormatScalar(valueElement)}'",
            "not_equal" => $"{field} != '{FormatScalar(valueElement)}'",
            "begins_with" => $"{field} LIKE '{FormatScalar(valueElement)}%'",
            "not_begins_with" => $"{field} NOT LIKE '{FormatScalar(valueElement)}%'",
            "ends_with" => $"{field} LIKE '%{FormatScalar(valueElement)}'",
            "not_ends_with" => $"{field} NOT LIKE '%{FormatScalar(valueElement)}'",
            "contains" => $"{field} LIKE '%{FormatScalar(valueElement)}%'",
            "not_contains" => $"{field} NOT LIKE '%{FormatScalar(valueElement)}%'",
            "is_empty" => $"{field} = ''",
            "is_not_empty" => $"{field} != ''",
            "is_null" => $"{field} IS NULL",
            "is_not_null" => $"{field} IS NOT NULL",
            "less" => $"{field} < {FormatScalar(valueElement)}",
            "less_or_equal" => $"{field} <= {FormatScalar(valueElement)}",
            "greater" => $"{field} > {FormatScalar(valueElement)}",
            "greater_or_equal" => $"{field} >= {FormatScalar(valueElement)}",
            "between" => FormatBetween(field!, valueElement, negate: false),
            "not_between" => FormatBetween(field!, valueElement, negate: true),
            "in" => $"{field} IN ({FormatQuotedList(valueElement)})",
            "not_in" => $"{field} NOT IN ({FormatQuotedList(valueElement)})",
            null => field,
            _ => $"{field} {op} {FormatScalar(valueElement)}",
        };
    }

    private static string FormatBetween(string field, JsonElement value, bool negate)
    {
        var keyword = negate ? "NOT BETWEEN" : "BETWEEN";

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Select(FormatScalar).ToArray();
            if (items.Length == 2)
            {
                return $"{field} {keyword} '{items[0]}' AND '{items[1]}'";
            }
        }

        return $"{field} {keyword} {FormatScalar(value)}";
    }

    private static string FormatQuotedList(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? string.Join(", ", value.EnumerateArray().Select(v => $"'{FormatScalar(v)}'"))
            : $"'{FormatScalar(value)}'";

    private static string FormatScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number or JsonValueKind.Null or JsonValueKind.Undefined => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
