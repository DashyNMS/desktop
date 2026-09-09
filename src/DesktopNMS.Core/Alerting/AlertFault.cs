namespace DesktopNMS.Core.Alerting;

/// <summary>How useful a fault field is for telling the reader what happened.</summary>
public enum AlertFaultFieldKind
{
    /// <summary>Names the thing that faulted: hostname, sysName, ifName.</summary>
    Identity = 0,

    /// <summary>Describes it: ifAlias, *_descr, *_msg.</summary>
    Description = 1,

    /// <summary>A value from the table the rule targets. The interesting part.</summary>
    Metric = 2,

    /// <summary>An operational value from the device record: status, uptime, last_ping.</summary>
    DeviceContext = 3,
}

/// <summary>One column of one matched row.</summary>
public sealed class AlertFaultField
{
    public AlertFaultField(string name, string value, AlertFaultFieldKind kind, bool isTrigger)
    {
        Name = name;
        Value = value;
        Kind = kind;
        IsTrigger = isTrigger;
    }

    /// <summary>The database column name, as LibreNMS's rule builder shows it.</summary>
    public string Name { get; }

    public string Value { get; }

    public AlertFaultFieldKind Kind { get; }

    /// <summary>True when the alert rule's condition tests this column: the reason it fired.</summary>
    public bool IsTrigger { get; }

    public override string ToString() => $"{Name} = {Value}";
}

/// <summary>
/// One row matched by the alert rule's query: a single interface, sensor,
/// mempool or whatever the rule targets, with the values that tripped it.
/// </summary>
public sealed class AlertFault
{
    public AlertFault(IReadOnlyList<AlertFaultField> fields)
    {
        Fields = fields;
        Identity = fields.Where(f => f.Kind == AlertFaultFieldKind.Identity).ToArray();
        Descriptions = fields.Where(f => f.Kind == AlertFaultFieldKind.Description).ToArray();

        TriggerFields = fields.Where(f => f.IsTrigger).ToArray();
        OtherFields = fields
            .Where(f => !f.IsTrigger && f.Kind is AlertFaultFieldKind.Metric or AlertFaultFieldKind.DeviceContext)
            .ToArray();
    }

    /// <summary>Every displayable field, ordered by usefulness.</summary>
    public IReadOnlyList<AlertFaultField> Fields { get; }

    public IReadOnlyList<AlertFaultField> Identity { get; }

    public IReadOnlyList<AlertFaultField> Descriptions { get; }

    /// <summary>
    /// The columns the rule's condition actually tests. Empty when the rule
    /// definition could not be read, in which case fall back to
    /// <see cref="OtherFields"/>.
    /// </summary>
    public IReadOnlyList<AlertFaultField> TriggerFields { get; }

    /// <summary>Everything else measured on the row, for when the trigger fields are not enough.</summary>
    public IReadOnlyList<AlertFaultField> OtherFields { get; }

    public bool HasTriggerFields => TriggerFields.Count > 0;

    public bool HasOtherFields => OtherFields.Count > 0;

    /// <summary>
    /// What to show by default: the columns the rule tests, or everything
    /// measured when the rule definition was unavailable.
    /// </summary>
    public IReadOnlyList<AlertFaultField> PrimaryFields => HasTriggerFields ? TriggerFields : OtherFields;

    /// <summary>What to show only when the reader asks for the full row.</summary>
    public IReadOnlyList<AlertFaultField> SecondaryFields =>
        HasTriggerFields ? OtherFields : Array.Empty<AlertFaultField>();

    public bool HasSecondaryFields => SecondaryFields.Count > 0;

    /// <summary>
    /// A one-line label for the faulting entity, e.g. "Gi0/0/1 - uplink to core".
    /// </summary>
    /// <remarks>
    /// Device identity columns are used only as a fallback. The device is
    /// already named above the fault list, so titling every card with the
    /// hostname would bury the one thing the card exists to say: which
    /// interface, sensor or pool actually faulted. A rule that targets the
    /// device itself, such as "device down", legitimately has nothing else, and
    /// falls back to the host name.
    /// </remarks>
    public string Title
    {
        get
        {
            var entity = Identity
                .Where(f => !LibreNmsSchema.IsDeviceIdentity(f.Name))
                .Select(f => f.Value)
                .Concat(Descriptions.Select(f => f.Value));

            var parts = Pick(entity);

            if (parts.Length == 0)
            {
                parts = Pick(Identity.Select(f => f.Value));
            }

            return parts.Length == 0 ? "Matched row" : string.Join(" - ", parts);
        }
    }

    private static string[] Pick(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();

    public override string ToString() => Title;
}

/// <summary>Everything DesktopNMS can say about why an alert is firing.</summary>
public sealed class AlertDetail
{
    public static AlertDetail Empty { get; } =
        new(Array.Empty<AlertFault>(), Array.Empty<AlertFault>(), Array.Empty<AlertFault>(), null, false);

    public AlertDetail(
        IReadOnlyList<AlertFault> faults,
        IReadOnlyList<AlertFault> added,
        IReadOnlyList<AlertFault> resolved,
        DateTime? recordedAt,
        bool ruleConditionKnown)
    {
        Faults = faults;
        Added = added;
        Resolved = resolved;
        RecordedAt = recordedAt;
        RuleConditionKnown = ruleConditionKnown;
    }

    /// <summary>Every row the rule matched when it last ran.</summary>
    public IReadOnlyList<AlertFault> Faults { get; }

    /// <summary>Rows that appeared since the previous evaluation, if LibreNMS recorded a diff.</summary>
    public IReadOnlyList<AlertFault> Added { get; }

    /// <summary>Rows that stopped matching since the previous evaluation.</summary>
    public IReadOnlyList<AlertFault> Resolved { get; }

    /// <summary>When the alert_log entry these came from was written (server local time).</summary>
    public DateTime? RecordedAt { get; }

    /// <summary>
    /// True when the rule definition was available, so trigger columns could be
    /// identified. False means every value is shown undifferentiated.
    /// </summary>
    public bool RuleConditionKnown { get; }

    public bool HasFaults => Faults.Count > 0;
}
