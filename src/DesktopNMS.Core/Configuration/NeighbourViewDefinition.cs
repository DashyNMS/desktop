using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Configuration;

/// <summary>Which part of a switch's LLDP/CDP neighbour a <see cref="NeighbourRule"/> tests - the parts LibreNMS keeps.</summary>
public enum NeighbourRuleField
{
    /// <summary>The name the neighbour announces (LLDP SysName) - usually its hostname.</summary>
    SystemName,

    /// <summary>What it announces about itself (LLDP System Descr) - typically the make, model and software version.</summary>
    SystemDescription,

    /// <summary>Its port as announced (LLDP PortId) - often its MAC address.</summary>
    PortId,

    /// <summary>"lldp", "cdp", ...</summary>
    Protocol,

    /// <summary>The name of the switch it's plugged into.</summary>
    Switch,

    /// <summary>The description (ifAlias) of the switch port it's plugged into, e.g. "AP".</summary>
    SwitchPortDescription,
}

public enum NeighbourRuleOperator
{
    Contains,
    StartsWith,
    Equals,
    DoesNotContain,

    /// <summary>A .NET regular expression, case-insensitive.</summary>
    Matches,
}

/// <summary>
/// One test in a <see cref="NeighbourViewDefinition"/>: a field, how to compare it, and what with. Case-insensitive.
/// </summary>
/// <remarks>
/// Stored as <c>{"field": "SystemDescription", "operator": "Contains", "value": "..."}</c>,
/// the enum names as strings. DashyNMS Mobile writes the same format (#197),
/// so a field or operator this version doesn't know - from mobile or a newer
/// desktop - must not stop the settings loading: it's kept as written, the
/// rule reports <see cref="IsSupported"/> false and never matches, and saving
/// writes it back unchanged.
/// </remarks>
public sealed class NeighbourRule
{
    private NeighbourRuleField _field = NeighbourRuleField.SystemDescription;
    private NeighbourRuleOperator _operator = NeighbourRuleOperator.Contains;
    private string? _unknownField;
    private string? _unknownOperator;

    [JsonIgnore]
    public NeighbourRuleField Field
    {
        get => _field;
        set
        {
            _field = value;
            _unknownField = null;
        }
    }

    [JsonIgnore]
    public NeighbourRuleOperator Operator
    {
        get => _operator;
        set
        {
            _operator = value;
            _unknownOperator = null;
        }
    }

    /// <summary><see cref="Field"/> as stored - or, for a field this version doesn't know, exactly what was stored.</summary>
    [JsonPropertyName("field")]
    public string FieldName
    {
        get => _unknownField ?? _field.ToString();
        set => _unknownField = TryParse(value, ref _field);
    }

    /// <summary><see cref="Operator"/> as stored - or, for an operator this version doesn't know, exactly what was stored.</summary>
    [JsonPropertyName("operator")]
    public string OperatorName
    {
        get => _unknownOperator ?? _operator.ToString();
        set => _unknownOperator = TryParse(value, ref _operator);
    }

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>False when the field or operator came from a newer version and can't be evaluated here.</summary>
    [JsonIgnore]
    public bool IsSupported => _unknownField is null && _unknownOperator is null;

    /// <summary>Anything else stored on the rule that this version doesn't model, kept so saving never strips it.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public NeighbourRule Clone() => new()
    {
        FieldName = FieldName,
        OperatorName = OperatorName,
        Value = Value,
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };

    /// <summary>Parses a stored enum name into <paramref name="target"/>; returns the text itself when it isn't one this version knows, or null when it is (or is blank).</summary>
    private static string? TryParse<TEnum>(string? text, ref TEnum target)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!char.IsDigit(text[0]) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            target = parsed;
            return null;
        }

        return text;
    }
}

/// <summary>
/// A user-made Neighbours tab view: a name, and the rules a switch's
/// LLDP/CDP neighbour has to meet to be listed in it - "System description
/// contains ...", say. Lets anyone filter their switches' LLDP results
/// into views of their own without the app knowing about each vendor.
/// </summary>
/// <remarks>
/// A shared format: DashyNMS Mobile creates and edits these in the same
/// <c>neighbourViews</c> list (#197) - <c>{"id", "name", "matchAll", "rules",
/// "showOnMap"}</c>, with each rule as described on <see cref="NeighbourRule"/>.
/// Only <c>id</c> and <c>name</c> are needed; anything missing takes the
/// defaults here. Tell the mobile repo before changing it.
/// </remarks>
public sealed class NeighbourViewDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New view";

    /// <summary>True: every rule has to match. False: any one will do.</summary>
    public bool MatchAll { get; set; } = true;

    public List<NeighbourRule> Rules { get; set; } = new();

    /// <summary>Draw this view's neighbours on the network map, joined to their switches - the ones LibreNMS doesn't already monitor as devices.</summary>
    public bool ShowOnMap { get; set; }

    /// <summary>Anything else stored on the view that this version doesn't model - from DashyNMS Mobile or a newer desktop - kept so saving never strips it (#197).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public NeighbourViewDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        MatchAll = MatchAll,
        Rules = Rules.Select(r => r.Clone()).ToList(),
        ShowOnMap = ShowOnMap,
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };

    public void Normalise()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = "Untitled view";
        }

        Rules ??= new List<NeighbourRule>();
        Rules.RemoveAll(r => r is null);
        foreach (var rule in Rules)
        {
            rule.Value ??= string.Empty;
        }
    }
}
