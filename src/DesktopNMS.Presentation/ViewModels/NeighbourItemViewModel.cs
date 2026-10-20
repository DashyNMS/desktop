using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>How a neighbour reads at a glance - drives its status dot and the Up/Down pills.</summary>
public enum NeighbourState
{
    Up,
    Down,

    /// <summary>Shut-down port, disabled or in-maintenance device or switch, or a neighbour LibreNMS no longer sees.</summary>
    Other,
}

/// <summary>Display names for view rules - shared by the tab and the view editor.</summary>
public static class NeighbourViewText
{
    public static string FieldName(NeighbourRuleField field) => field switch
    {
        NeighbourRuleField.SystemName => "System name",
        NeighbourRuleField.SystemDescription => "System description",
        NeighbourRuleField.PortId => "Port ID",
        NeighbourRuleField.Protocol => "Protocol",
        NeighbourRuleField.Switch => "Switch",
        NeighbourRuleField.SwitchPortDescription => "Switch port description",
        _ => field.ToString(),
    };

    public static string OperatorName(NeighbourRuleOperator op) => op switch
    {
        NeighbourRuleOperator.Contains => "contains",
        NeighbourRuleOperator.StartsWith => "starts with",
        NeighbourRuleOperator.Equals => "equals",
        NeighbourRuleOperator.DoesNotContain => "does not contain",
        NeighbourRuleOperator.Matches => "matches regex",
        _ => op.ToString(),
    };

    /// <summary>"System description contains X and switch starts with Y".</summary>
    public static string DescribeRules(NeighbourViewDefinition view)
    {
        var rules = view.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rules.Count == 0)
        {
            return "No rules yet - edit the neighbourhood to add some.";
        }

        var text = string.Join(view.MatchAll ? " and " : " or ", rules.Select(r => r.IsSupported
            ? $"{FieldName(r.Field).ToLowerInvariant()} {OperatorName(r.Operator)} \"{r.Value.Trim()}\""
            : "a rule from a newer version of DashyNMS"));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>One neighbour row.</summary>
public sealed class NeighbourItemViewModel
{
    /// <param name="arpIp">The IP the fleet's ARP tables have for its MAC, for one LibreNMS doesn't monitor.</param>
    public NeighbourItemViewModel(Neighbour neighbour, Port? port, Device? @switch, Device? device, string? arpIp = null)
    {
        Neighbour = neighbour;
        Port = port;
        Device = device;
        SwitchName = @switch?.BestName ?? $"device {neighbour.SwitchDeviceId}";
        IpText = device?.Ip ?? arpIp ?? string.Empty;

        // ifLastChange is the switch's sysUpTime (hundredths of a second) at
        // the change; the switch's uptime now, less that, is how long ago.
        if (port?.IfLastChange is { } changedAt && @switch is { Status: true, Uptime: > 0 } polled && polled.Uptime >= changedAt / 100)
        {
            SecondsSinceChange = polled.Uptime - (changedAt / 100);
        }

        // A switch that's down (or disabled) isn't being polled, so its
        // ports keep their last reading - often weeks-old "up".
        IsSwitchDown = @switch is { Status: false, Disabled: false };
        var switchDisabled = @switch is { Disabled: true };

        // A row is one neighbour on one switch port, so the link comes
        // first: a switch that's down, or a port that's down, means this
        // link is down - even when the neighbour is up somewhere else (seen
        // on a second switch, or an old link LibreNMS hasn't dropped).
        (State, StateText) =
            IsSwitchDown ? (NeighbourState.Down, "Down")
            : switchDisabled ? (NeighbourState.Other, "Switch disabled")
            : !neighbour.Active ? (NeighbourState.Other, "Not seen")
            : port is not null && string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? (NeighbourState.Other, "Port shut down")
            : port is not null && !port.IsUp ? (NeighbourState.Down, "Down")

            // The link's up (or its port unknown): a neighbour LibreNMS
            // monitors itself then has the last word on its own state.
            : device is not null ? device.State switch
            {
                DeviceState.Up => (NeighbourState.Up, "Up"),
                DeviceState.Down => (NeighbourState.Down, "Down"),
                DeviceState.Maintenance => (NeighbourState.Other, "Maintenance"),
                DeviceState.Disabled => (NeighbourState.Other, "Disabled"),
                DeviceState.Ignored => (NeighbourState.Other, "Ignored"),
                _ => (NeighbourState.Other, "Unknown"),
            }
            : port is null ? (NeighbourState.Other, "Unknown port")
            : (NeighbourState.Up, "Up");
    }

    public Neighbour Neighbour { get; }

    public Port? Port { get; }

    /// <summary>The LibreNMS device this neighbour is, when LibreNMS monitors it.</summary>
    public Device? Device { get; }

    public bool IsMonitored => Neighbour.IsMonitored;

    /// <summary>Tells two rows apart across a reload - a neighbour can be on more than one port.</summary>
    public string Key => Neighbour.Name + "|" + (Neighbour.Mac ?? Neighbour.RemotePort) + "@" + Neighbour.SwitchPortId.ToString(CultureInfo.InvariantCulture);

    public string Name => Neighbour.Name;

    public string DescriptionText => Neighbour.Description ?? string.Empty;

    public string MacText => Neighbour.Mac ?? string.Empty;

    public int SwitchDeviceId => Neighbour.SwitchDeviceId;

    public string SwitchName { get; }

    public bool IsSwitchDown { get; }

    /// <summary>This link is up - its switch is polled and its port is up - whatever the neighbour itself is doing.</summary>
    public bool IsLinkUp => !IsSwitchDown && Neighbour.Active && Port is { } port && port.IsUp;

    public string PortText => Port?.DisplayName ?? "-";

    /// <summary>The port's ifName - what LibreNMS's port graph route takes.</summary>
    public string? PortIfName => string.IsNullOrWhiteSpace(Port?.IfName) ? null : Port.IfName;

    public NeighbourState State { get; }

    public string StateText { get; }

    /// <summary>The same colours as everywhere else: green up, red down, grey otherwise.</summary>
    public AlertSeverity Severity => State switch
    {
        NeighbourState.Up => AlertSeverity.Ok,
        NeighbourState.Down => AlertSeverity.Critical,
        _ => AlertSeverity.Unknown,
    };

    public string SpeedText => !IsSwitchDown && Port?.IfSpeed is { } speed && speed > 0 ? LinkUtilisation.Rate(speed) : "-";

    /// <summary>The port's live figures mean something - its switch is polled and it's up. Otherwise they're stale or zero, and show as "-".</summary>
    private bool HasLiveFigures => Port is { } port && !IsSwitchDown && port.IsUp;

    /// <summary>The neighbour's IP: its LibreNMS device's, or from the fleet's ARP tables by its MAC.</summary>
    public string IpText { get; }

    /// <summary>Into the switch port - traffic from the neighbour. Sorts by <see cref="InBps"/>.</summary>
    public double InBps => HasLiveFigures ? (Port!.IfInOctetsRate ?? 0) * 8 : -1;

    public string InText => HasLiveFigures ? Rate(InBps) : "-";

    /// <summary>Out of the switch port - traffic to the neighbour.</summary>
    public double OutBps => HasLiveFigures ? (Port!.IfOutOctetsRate ?? 0) * 8 : -1;

    public string OutText => HasLiveFigures ? Rate(OutBps) : "-";

    /// <summary>In and out errors per second on the switch port, together.</summary>
    public double ErrorsPerSecond => HasLiveFigures ? (Port!.IfInErrorsRate ?? 0) + (Port.IfOutErrorsRate ?? 0) : -1;

    public string ErrorsText => HasLiveFigures ? PerSecond(ErrorsPerSecond) : "-";

    public string ErrorsToolTip => HasLiveFigures
        ? $"In {PerSecond(Port!.IfInErrorsRate ?? 0)}, out {PerSecond(Port.IfOutErrorsRate ?? 0)}"
        : "No live figures - the port or its switch is down.";

    public double PacketsInPerSecond => HasLiveFigures ? Port!.IfInUcastPktsRate ?? 0 : -1;

    public string PacketsInText => HasLiveFigures ? PerSecond(PacketsInPerSecond) : "-";

    public double PacketsOutPerSecond => HasLiveFigures ? Port!.IfOutUcastPktsRate ?? 0 : -1;

    public string PacketsOutText => HasLiveFigures ? PerSecond(PacketsOutPerSecond) : "-";

    /// <summary>The busier direction as a share of the port's speed.</summary>
    public double UtilisationPercent => HasLiveFigures && Port!.IfSpeed is { } speed && speed > 0
        ? Math.Min(100, Math.Max(InBps, OutBps) / speed * 100)
        : -1;

    public string UtilisationText => UtilisationPercent >= 0 ? UtilisationPercent.ToString(UtilisationPercent < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + "%" : "-";

    /// <summary>The switch port's own description (ifAlias).</summary>
    public string PortDescriptionText => Port?.IfAlias?.Trim() ?? string.Empty;

    public string DuplexText => Port?.IfDuplex?.Trim().ToLowerInvariant() switch
    {
        "fullduplex" or "full" => "Full",
        "halfduplex" or "half" => "Half",
        _ => "-",
    };

    public string VlanText => Port?.IfVlan is { } vlan && vlan > 0 ? vlan.ToString(CultureInfo.CurrentCulture) : "-";

    public string MtuText => Port?.IfMtu is { } mtu && mtu > 0 ? mtu.ToString(CultureInfo.CurrentCulture) : "-";

    /// <summary>How long since the port last went up or down, from the switch's uptime - null if it can't be told.</summary>
    public long? SecondsSinceChange { get; }

    public string LastChangeText => SecondsSinceChange is { } seconds ? RoutingSectionViewModel.FormatDuration(seconds) + " ago" : "-";

    public string ProtocolText => Neighbour.Protocol?.ToUpperInvariant() ?? string.Empty;

    /// <summary>The port the neighbour announces - often its MAC.</summary>
    public string AnnouncedPortText => Neighbour.RemotePort ?? string.Empty;

    public string ToolTip
    {
        get
        {
            var text = Neighbour.IsUnnamed
                ? $"This doesn't announce a name - its port/MAC is {Neighbour.RemotePort ?? "unknown"}."
                : Name;
            text += $" Plugged into {SwitchName} {PortText}.";
            if (IsSwitchDown)
            {
                text += $" {SwitchName} is down, so this link counts as down too.";
            }

            if (IsMonitored)
            {
                text += " LibreNMS monitors it - click to open it.";
            }

            return text;
        }
    }

    public bool Matches(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        var t = term.Trim();
        return Name.Contains(t, StringComparison.OrdinalIgnoreCase)
            || DescriptionText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || SwitchName.Contains(t, StringComparison.OrdinalIgnoreCase)
            || PortText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || MacText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || IpText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || PortDescriptionText.Contains(t, StringComparison.OrdinalIgnoreCase)
            || StateText.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rate(double bps) => bps <= 0 ? "0 bps" : LinkUtilisation.Rate(bps);

    /// <summary>"0", "0.2/s", "8,633/s".</summary>
    private static string PerSecond(double value) =>
        value <= 0 ? "0" : value.ToString(value < 10 ? "0.#" : "N0", CultureInfo.CurrentCulture) + "/s";
}

/// <summary>
/// One chip in the Neighbours tab's row (#221): All (no definition), or a
/// neighbourhood - a <see cref="NeighbourViewDefinition"/>, stored as before
/// so mobile reads the same ones - with how many neighbours it holds.
/// </summary>
public sealed class NeighbourhoodChipViewModel : ObservableObject
{
    private bool _isSelected;

    public NeighbourhoodChipViewModel(NeighbourViewDefinition? definition, string name, int? count, bool isSelected, bool isFirst, bool isLast)
    {
        Definition = definition;
        Name = name;
        Count = count;
        _isSelected = isSelected;
        IsFirst = isFirst;
        IsLast = isLast;
    }

    /// <summary>Null for the All chip.</summary>
    public NeighbourViewDefinition? Definition { get; }

    public bool IsNeighbourhood => Definition is not null;

    public string Name { get; }

    /// <summary>How many neighbours it holds right now - null until they've loaded.</summary>
    public int? Count { get; }

    public string CountText => Count?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsFirst { get; }

    public bool IsLast { get; }

    public bool CanMoveLeft => IsNeighbourhood && !IsFirst;

    public bool CanMoveRight => IsNeighbourhood && !IsLast;

    /// <summary>"Show on network map", ticked in the chip's menu.</summary>
    public bool ShowsOnMap => Definition?.ShowOnMap == true;

    /// <summary>The neighbourhood's rules, as its tooltip.</summary>
    public string? RulesText => Definition is { } definition ? NeighbourViewText.DescribeRules(definition) : "Every neighbour your switches see over LLDP or CDP";
}
