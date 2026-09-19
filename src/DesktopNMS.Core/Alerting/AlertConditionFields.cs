using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopNMS.Core.Alerting;

/// <summary>One operator a condition field can use in the rule builder - value is what LibreNMS's builder JSON expects, label is what the UI shows.</summary>
public sealed record AlertConditionOperator(string Value, string Label)
{
    /// <summary>Fixes this app's known ComboBox closed-display quirk (the shared style falls back to .ToString() rather than DisplayMemberPath) - see GraphType/DeviceNameOption for the same workaround.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// One selectable field in the rule builder's condition editor.
/// <see cref="Label"/> is always the raw, fully-qualified field name (e.g.
/// "devices.sysName") - matching LibreNMS's own rule editor exactly, which
/// shows these dotted names verbatim rather than a friendlier paraphrase.
/// Admins working with LibreNMS rules already know these names, so showing
/// anything else is a mismatch, not an improvement.
/// </summary>
public sealed record AlertConditionField(
    string Field,
    string Type,
    string Input,
    IReadOnlyList<AlertConditionOperator> Operators,
    IReadOnlyList<string> Values)
{
    public string Label => Field;

    /// <summary>The table (or "macros") this field belongs to - the part before the first dot.</summary>
    public string Table
    {
        get
        {
            var dot = Field.IndexOf('.');
            return dot < 0 ? Field : Field[..dot];
        }
    }

    public override string ToString() => Label;
}

/// <summary>A named group of fields, e.g. "devices" or "sensors", for the rule builder's field picker.</summary>
public sealed record AlertConditionFieldGroup(string Name, IReadOnlyList<AlertConditionField> Fields);

/// <summary>
/// The complete set of fields LibreNMS's own rule builder offers, generated
/// from LibreNMS's source rather than hand-curated from its docs (the docs'
/// "Entities" page is explicitly incomplete and was missing whole tables and
/// obvious columns like <c>devices.uptime</c>).
/// </summary>
/// <remarks>
/// <para>
/// LibreNMS builds its filter list at runtime in
/// <c>LibreNMS/Alerting/QueryBuilderFilter.php</c>: it walks
/// <c>resources/definitions/schema/db_schema.yaml</c>, keeps every table that
/// <c>LibreNMS\DB\Schema::getAllRelationshipPaths()</c> can trace back to
/// <c>devices</c> (minus <c>device_group_device</c>/<c>alerts</c>/<c>alert_log</c>),
/// drops each table's own <c>device_id</c> column and any binary/blob column,
/// then prepends the <c>alert.macros.rule.*</c> macros from
/// <c>resources/definitions/config_definitions.json</c> (skipping the
/// <c>past_&lt;n&gt;m</c> ones, which aren't plain field comparisons).
/// <see cref="AlertConditionFieldCatalog"/> is that algorithm replayed
/// offline against those two files - 115 tables, 1,385 fields.
/// </para>
/// <para>
/// To regenerate after a LibreNMS schema change, re-run that port against the
/// current <c>db_schema.yaml</c> and <c>config_definitions.json</c>. Column
/// types follow LibreNMS's own mapping, including its quirks: every integer
/// column is typed <c>string</c> (there's a <c>TODO</c> in its source about
/// that), <c>char</c>/<c>decimal</c>/<c>longtext</c>/<c>mediumtext</c> columns
/// are dropped entirely because its prefix checks don't match them, and enum
/// columns become radio fields restricted to <c>equal</c>.
/// </para>
/// </remarks>
public static class AlertConditionFields
{
    // Operator sets below mirror the list LibreNMS passes to jQuery
    // QueryBuilder in includes/html/modal/new_alert_rule.inc.php, which
    // deliberately overrides the library's defaults: less/greater/regex are
    // re-declared with apply_to including 'string' (so text columns can be
    // compared numerically), and in/not_in are left out entirely. Order here
    // is LibreNMS's order, so the dropdown reads the same as the web UI's.
    //
    // between/not_between are the one intentional omission: they carry two
    // values and this editor's condition row has a single value box. A rule
    // authored in the web UI that uses them still round-trips untouched via
    // the raw-JSON fallback.

    private static readonly AlertConditionOperator Equal = new("equal", "equal");
    private static readonly AlertConditionOperator NotEqual = new("not_equal", "not equal");
    private static readonly AlertConditionOperator BeginsWith = new("begins_with", "begins with");
    private static readonly AlertConditionOperator NotBeginsWith = new("not_begins_with", "doesn't begin with");
    private static readonly AlertConditionOperator Contains = new("contains", "contains");
    private static readonly AlertConditionOperator NotContains = new("not_contains", "doesn't contain");
    private static readonly AlertConditionOperator EndsWith = new("ends_with", "ends with");
    private static readonly AlertConditionOperator NotEndsWith = new("not_ends_with", "doesn't end with");
    private static readonly AlertConditionOperator IsEmpty = new("is_empty", "is empty");
    private static readonly AlertConditionOperator IsNotEmpty = new("is_not_empty", "is not empty");
    private static readonly AlertConditionOperator IsNull = new("is_null", "is null");
    private static readonly AlertConditionOperator IsNotNull = new("is_not_null", "is not null");
    private static readonly AlertConditionOperator Less = new("less", "less");
    private static readonly AlertConditionOperator LessOrEqual = new("less_or_equal", "less or equal");
    private static readonly AlertConditionOperator Greater = new("greater", "greater");
    private static readonly AlertConditionOperator GreaterOrEqual = new("greater_or_equal", "greater or equal");
    private static readonly AlertConditionOperator Regex = new("regex", "regex");
    private static readonly AlertConditionOperator NotRegex = new("not_regex", "not regex");

    private static readonly IReadOnlyList<AlertConditionOperator> StringOperators = new[]
    {
        Equal, NotEqual,
        BeginsWith, NotBeginsWith, Contains, NotContains, EndsWith, NotEndsWith,
        IsEmpty, IsNotEmpty, IsNull, IsNotNull,
        Less, LessOrEqual, Greater, GreaterOrEqual,
        Regex, NotRegex,
    };

    private static readonly IReadOnlyList<AlertConditionOperator> NumericOperators = new[]
    {
        Equal, NotEqual,
        IsNull, IsNotNull,
        Less, LessOrEqual, Greater, GreaterOrEqual,
        Regex, NotRegex,
    };

    private static readonly IReadOnlyList<AlertConditionOperator> DateTimeOperators = new[]
    {
        Equal, NotEqual,
        IsNull, IsNotNull,
        Less, LessOrEqual, Greater, GreaterOrEqual,
    };

    /// <summary>Radio fields (enum columns and yes/no macros) - LibreNMS pins these to <c>equal</c> alone.</summary>
    private static readonly IReadOnlyList<AlertConditionOperator> EqualOnly = new[] { Equal };

    private static readonly IReadOnlyList<string> NoValues = Array.Empty<string>();

    /// <summary>The two values LibreNMS offers for a yes/no macro, in its own order.</summary>
    private static readonly IReadOnlyList<string> YesNoValues = new[] { "1", "0" };

    private static AlertConditionField Str(string field) => new(field, "string", "text", StringOperators, NoValues);

    public static IReadOnlyList<AlertConditionFieldGroup> Groups { get; } = Parse();

    private static IReadOnlyList<AlertConditionFieldGroup> Parse()
    {
        var fields = new List<AlertConditionField>();

        foreach (var line in AlertConditionFieldCatalog.Data.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split('|');
            var name = parts[0];
            var kind = parts.Length > 1 ? parts[1] : "s";

            fields.Add(kind switch
            {
                "d" => new AlertConditionField(name, "datetime", "text", DateTimeOperators, NoValues),
                "i" => new AlertConditionField(name, "integer", "text", NumericOperators, NoValues),
                "r" => new AlertConditionField(name, "integer", "radio", EqualOnly, YesNoValues),
                "e" => new AlertConditionField(name, "integer", "radio", EqualOnly, parts.Length > 2 ? parts[2].Split(',') : NoValues),
                _ => Str(name),
            });
        }

        return fields
            .GroupBy(f => f.Table)
            .Select(g => new AlertConditionFieldGroup(g.Key, g.ToList()))
            .ToList();
    }

    private static readonly IReadOnlyDictionary<string, AlertConditionField> ByField =
        Groups.SelectMany(g => g.Fields).ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);

    public static AlertConditionField? Find(string? field) =>
        field is not null && ByField.TryGetValue(field, out var match) ? match : null;

    /// <summary>
    /// Falls back to a bare, generic string entry (so the field still renders
    /// and round-trips) when <paramref name="field"/> isn't in the catalog -
    /// e.g. a rule written against a custom column, or one added to LibreNMS
    /// after this catalog was last generated.
    /// </summary>
    public static AlertConditionField Resolve(string field) => Find(field) ?? Str(field);
}
