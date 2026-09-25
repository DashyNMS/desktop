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

/// <summary>One test in a <see cref="NeighbourViewDefinition"/>: a field, how to compare it, and what with. Case-insensitive.</summary>
public sealed class NeighbourRule
{
    public NeighbourRuleField Field { get; set; } = NeighbourRuleField.SystemDescription;

    public NeighbourRuleOperator Operator { get; set; } = NeighbourRuleOperator.Contains;

    public string Value { get; set; } = string.Empty;

    public NeighbourRule Clone() => new() { Field = Field, Operator = Operator, Value = Value };
}

/// <summary>
/// A user-made Neighbours tab view: a name, and the rules a switch's
/// LLDP/CDP neighbour has to meet to be listed in it - "System description
/// contains ...", say. Lets anyone filter their switches' LLDP results
/// into views of their own without the app knowing about each vendor.
/// </summary>
public sealed class NeighbourViewDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New view";

    /// <summary>True: every rule has to match. False: any one will do.</summary>
    public bool MatchAll { get; set; } = true;

    public List<NeighbourRule> Rules { get; set; } = new();

    /// <summary>Draw this view's neighbours on the network map, joined to their switches - the ones LibreNMS doesn't already monitor as devices.</summary>
    public bool ShowOnMap { get; set; }

    public NeighbourViewDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        MatchAll = MatchAll,
        Rules = Rules.Select(r => r.Clone()).ToList(),
        ShowOnMap = ShowOnMap,
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
