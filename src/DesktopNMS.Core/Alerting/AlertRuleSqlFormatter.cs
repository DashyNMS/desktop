using System.Globalization;
using System.Text.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Renders a builder tree as the condition text LibreNMS's own rule list
/// shows - a port of <c>QueryBuilderParser::toSql(false)</c>: macros left
/// unexpanded, no SELECT/join preamble, sub-groups in parentheses, LIKE
/// patterns single-quoted, other non-numeric values double-quoted and
/// numbers bare. The inverse of <see cref="AlertRuleSqlImporter"/>.
/// </summary>
public static class AlertRuleSqlFormatter
{
    private static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>
    {
        ["equal"] = "=",
        ["not_equal"] = "!=",
        ["less"] = "<",
        ["less_or_equal"] = "<=",
        ["greater"] = ">",
        ["greater_or_equal"] = ">=",
        ["between"] = "BETWEEN",
        ["not_between"] = "NOT BETWEEN",
        ["begins_with"] = "LIKE",
        ["not_begins_with"] = "NOT LIKE",
        ["contains"] = "LIKE",
        ["not_contains"] = "NOT LIKE",
        ["ends_with"] = "LIKE",
        ["not_ends_with"] = "NOT LIKE",
        ["is_empty"] = "=",
        ["is_not_empty"] = "!=",
        ["is_null"] = "IS NULL",
        ["is_not_null"] = "IS NOT NULL",
        ["regex"] = "REGEXP",
        ["not_regex"] = "NOT REGEXP",
        ["in"] = "IN",
        ["not_in"] = "NOT IN",
    };

    /// <summary>Value templates, with <c>?</c> standing for each value in turn - verbatim from LibreNMS.</summary>
    private static readonly IReadOnlyDictionary<string, string> ValueTemplates = new Dictionary<string, string>
    {
        ["between"] = "? AND ?",
        ["not_between"] = "? AND ?",
        ["begins_with"] = "'?%'",
        ["not_begins_with"] = "'?%'",
        ["contains"] = "'%?%'",
        ["not_contains"] = "'%?%'",
        ["ends_with"] = "'%?'",
        ["not_ends_with"] = "'%?'",
        ["is_null"] = "",
        ["is_not_null"] = "",
        ["is_empty"] = "''",
        ["is_not_empty"] = "''",
    };

    /// <summary>Formats stored builder JSON; null when it isn't a parseable group.</summary>
    public static string? Format(string? builderJson)
    {
        if (string.IsNullOrWhiteSpace(builderJson))
        {
            return null;
        }

        try
        {
            var node = JsonSerializer.Deserialize<AlertConditionNode>(builderJson);
            return node is { IsGroup: true } ? Format(node) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Format(AlertConditionNode node) => node.IsGroup ? FormatGroup(node, wrap: false) : FormatLeaf(node);

    private static string FormatGroup(AlertConditionNode group, bool wrap)
    {
        var parts = group.Rules!.Select(child => child.IsGroup ? FormatGroup(child, wrap: true) : FormatLeaf(child));
        var sql = string.Join($" {group.Condition ?? "AND"} ", parts);
        return wrap ? $"({sql})" : sql;
    }

    private static string FormatLeaf(AlertConditionNode leaf)
    {
        var op = leaf.Operator ?? "equal";
        var sqlOp = Operators.GetValueOrDefault(op, op.ToUpperInvariant());
        var values = leaf.ValueList;
        string value;

        if (values.Count == 1 && values[0].Length > 1 && values[0].StartsWith('`') && values[0].EndsWith('`'))
        {
            // A backticked value is a field reference, passed through bare.
            value = values[0].Trim('`');
        }
        else if (ValueTemplates.TryGetValue(op, out var template))
        {
            var queue = new Queue<string>(values);
            value = string.Concat(template.Select(c => c == '?' ? (queue.Count > 0 ? queue.Dequeue() : string.Empty) : c.ToString()));
        }
        else if (op is "in" or "not_in")
        {
            value = "(" + string.Join(", ", values.Select(Quote)) + ")";
        }
        else
        {
            value = values.Count == 0 ? string.Empty : Quote(values[0]);
        }

        return $"{leaf.Field} {sqlOp} {value}".Trim();
    }

    /// <summary>LibreNMS wraps non-numeric values in double quotes and leaves numbers bare.</summary>
    private static string Quote(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : $"\"{value}\"";
}
