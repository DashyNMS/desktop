using System.Collections.Generic;
using System.Linq;

namespace DesktopNMS.Core.Alerting;

/// <summary>One operator a condition field can use in the rule builder - value is what LibreNMS's builder JSON expects, label is what the UI shows.</summary>
public sealed record AlertConditionOperator(string Value, string Label)
{
    /// <summary>Fixes this app's known ComboBox closed-display quirk (the shared style falls back to .ToString() rather than DisplayMemberPath) - see GraphType/DeviceNameOption for the same workaround.</summary>
    public override string ToString() => Label;
}

/// <summary>One selectable field in the rule builder's condition editor.</summary>
public sealed record AlertConditionField(string Field, string Label, string Type, string Input, IReadOnlyList<AlertConditionOperator> Operators)
{
    public override string ToString() => Label;
}

/// <summary>A named group of fields, e.g. "Devices" or "Sensors", for the rule builder's field picker.</summary>
public sealed record AlertConditionFieldGroup(string Name, IReadOnlyList<AlertConditionField> Fields);

/// <summary>
/// A curated catalog of common alert rule condition fields, sourced from
/// LibreNMS's own "Entities" and "Macros" documentation pages - the same
/// hardcoded approach LibreNMS's own web UI takes (its jQuery QueryBuilder
/// config is a fixed field list, not fetched from an API). This is
/// deliberately "common fields, not exhaustive" - LibreNMS's own docs make
/// the same caveat ("This list is not complete. For the full list, read the
/// MySQL database schema."). A rule whose builder references a field not in
/// this catalog still round-trips correctly (see
/// <see cref="Models.AlertConditionNode"/>/the rule editor's own handling of
/// an unknown field), just without a friendly label or operator list.
/// </summary>
public static class AlertConditionFields
{
    private static readonly IReadOnlyList<AlertConditionOperator> StringOperators = new[]
    {
        new AlertConditionOperator("equal", "is"),
        new AlertConditionOperator("not_equal", "is not"),
        new AlertConditionOperator("contains", "contains"),
        new AlertConditionOperator("begins_with", "begins with"),
        new AlertConditionOperator("ends_with", "ends with"),
    };

    private static readonly IReadOnlyList<AlertConditionOperator> NumericOperators = new[]
    {
        new AlertConditionOperator("equal", "="),
        new AlertConditionOperator("not_equal", "!="),
        new AlertConditionOperator("greater", ">"),
        new AlertConditionOperator("greater_or_equal", ">="),
        new AlertConditionOperator("less", "<"),
        new AlertConditionOperator("less_or_equal", "<="),
    };

    private static readonly IReadOnlyList<AlertConditionOperator> BooleanOperators = new[]
    {
        new AlertConditionOperator("equal", "is"),
    };

    private static AlertConditionField Str(string field, string label) => new(field, label, "string", "text", StringOperators);

    private static AlertConditionField Num(string field, string label) => new(field, label, "integer", "text", NumericOperators);

    private static AlertConditionField Bool(string field, string label) => new(field, label, "integer", "radio", BooleanOperators);

    public static IReadOnlyList<AlertConditionFieldGroup> Groups { get; } = new[]
    {
        new AlertConditionFieldGroup("Devices", new[]
        {
            Str("devices.hostname", "Hostname"),
            Str("devices.sysName", "sysName"),
            Str("devices.sysDescr", "sysDescr"),
            Str("devices.hardware", "Hardware"),
            Str("devices.version", "OS version"),
            Str("devices.location", "Location"),
            Str("devices.type", "Device type"),
            Bool("devices.status", "Status is up"),
            Bool("devices.ignore", "Is ignored"),
            Bool("devices.disabled", "Is disabled"),
        }),
        new AlertConditionFieldGroup("Device stats", new[]
        {
            Num("device_stats.ping_loss_last", "Ping loss at last poll (%)"),
            Num("device_stats.ping_loss_avg", "Average ping loss (%)"),
            Num("device_stats.ping_rtt_last", "Ping RTT at last poll (ms)"),
            Num("device_stats.ping_rtt_avg", "Average ping RTT (ms)"),
        }),
        new AlertConditionFieldGroup("Ports", new[]
        {
            Str("ports.ifDescr", "Interface description"),
            Str("ports.ifName", "Interface name"),
            Num("ports.ifSpeed", "Speed (bps)"),
            Str("ports.ifOperStatus", "Operational status"),
            Str("ports.ifAdminStatus", "Administrative status"),
            Str("ports.ifDuplex", "Duplex"),
        }),
        new AlertConditionFieldGroup("Processors", new[]
        {
            Num("processors.processor_usage", "Usage (%)"),
            Str("processors.processor_descr", "Description"),
        }),
        new AlertConditionFieldGroup("Storage", new[]
        {
            Str("storage.storage_descr", "Description"),
            Num("storage.storage_perc", "Usage (%)"),
        }),
        new AlertConditionFieldGroup("Sensors", new[]
        {
            Str("sensors.sensor_class", "Sensor class"),
            Str("sensors.sensor_desc", "Description"),
            Num("sensors.sensor_current", "Current value"),
            Num("sensors.sensor_prev", "Previous value"),
        }),
        new AlertConditionFieldGroup("Macros", new[]
        {
            Bool("macros.device_up", "Device is up"),
            Bool("macros.device_down", "Device is down"),
            Bool("macros.port_up", "Port is up"),
            Bool("macros.port_down", "Port is down"),
        }),
    };

    private static readonly IReadOnlyDictionary<string, AlertConditionField> ByField =
        Groups.SelectMany(g => g.Fields).ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);

    public static AlertConditionField? Find(string? field) =>
        field is not null && ByField.TryGetValue(field, out var match) ? match : null;

    /// <summary>
    /// Falls back to a bare, generic string entry (so the field still renders
    /// and round-trips) when <paramref name="field"/> isn't in the catalog -
    /// e.g. a rule authored in the LibreNMS web UI against a column this
    /// curated list doesn't cover.
    /// </summary>
    public static AlertConditionField Resolve(string field) => Find(field) ?? Str(field, field);
}
