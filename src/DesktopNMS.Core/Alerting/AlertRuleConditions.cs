using System.Text.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// The columns an alert rule actually tests, pulled out of the rule definition.
/// </summary>
/// <remarks>
/// This is what makes "why did this fire" answerable. A rule's query selects
/// every column of every joined table, so the fault row is mostly noise; the
/// rule's condition names the handful of columns that matter.
/// </remarks>
public static class AlertRuleConditions
{
    /// <summary>
    /// Extracts the bare column names referenced by a rule.
    /// </summary>
    /// <remarks>
    /// LibreNMS stores the condition twice: <c>builder</c> holds jQuery
    /// QueryBuilder JSON with a <c>field</c> per condition (nested under
    /// <c>rules</c>, recursively), and <c>rule</c> holds the older
    /// <c>%ports.ifInErrors_delta &gt; "500"</c> string form. Fields are
    /// qualified as <c>table.column</c>, but fault rows are keyed by bare
    /// column name, so the table prefix is dropped.
    /// </remarks>
    public static IReadOnlySet<string> ExtractFields(AlertRule? rule)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rule is null)
        {
            return fields;
        }

        AddFromBuilder(rule.Builder, fields);

        if (fields.Count == 0)
        {
            AddFromLegacyRule(rule.Rule, fields);
        }

        return fields;
    }

    private static void AddFromBuilder(string? builderJson, HashSet<string> fields)
    {
        if (string.IsNullOrWhiteSpace(builderJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(builderJson);
            Walk(document.RootElement, fields, depth: 0);
        }
        catch (JsonException)
        {
            // An unparsable builder is not worth failing over; the legacy
            // string form is tried next.
        }
    }

    private static void Walk(JsonElement element, HashSet<string> fields, int depth)
    {
        // Guards against a pathological or hand-edited rule definition.
        if (depth > 24)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String)
                {
                    AddColumn(field.GetString(), fields);
                }

                if (element.TryGetProperty("rules", out var rules))
                {
                    Walk(rules, fields, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, fields, depth + 1);
                }

                break;
        }
    }

    private static void AddFromLegacyRule(string? rule, HashSet<string> fields)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return;
        }

        // Legacy form marks each column reference with a leading %, e.g.
        // "%devices.status = 0 && %macros.device_down = 1".
        var index = 0;

        while (index < rule.Length)
        {
            var start = rule.IndexOf('%', index);
            if (start < 0 || start == rule.Length - 1)
            {
                return;
            }

            var end = start + 1;
            while (end < rule.Length && (char.IsLetterOrDigit(rule[end]) || rule[end] is '_' or '.'))
            {
                end++;
            }

            AddColumn(rule[(start + 1)..end], fields);
            index = end + 1;
        }
    }

    private static void AddColumn(string? qualified, HashSet<string> fields)
    {
        if (string.IsNullOrWhiteSpace(qualified))
        {
            return;
        }

        var dot = qualified.LastIndexOf('.');
        var column = dot >= 0 && dot < qualified.Length - 1 ? qualified[(dot + 1)..] : qualified;

        // "macros.device_down" and friends are rule helpers, not row columns,
        // but they are harmless in the set: nothing in a fault row matches them.
        if (column.Length > 0)
        {
            fields.Add(column);
        }
    }
}
