using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Search;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One endpoint, laid out the way the page reads (#57): the answer first -
/// where it plugs in - then its addresses, the switch ports that learned it,
/// its subnet and the routers that know it.
/// </summary>
public sealed class EndpointViewModel : ObservableObject
{
    /// <summary>Switch ports shown before "+N more"; LibreNMS shows five too.</summary>
    public const int LocationsShown = 5;

    /// <summary>Subnet interfaces shown; the gateway comes first, and the rest are counted.</summary>
    public const int SubnetInterfacesShown = 5;

    private readonly EndpointRecord _record;
    private readonly IReadOnlySet<SearchSource> _blind;
    private bool _showAllLocations;
    private bool _showAllSubnet;
    private bool _showAllConnections;
    private bool _connectionsFailed;
    private IReadOnlyList<DeviceNeighbourItemViewModel>? _allConnections;

    public EndpointViewModel(EndpointRecord record, MacAddressFormat macFormat, IWindowService windows, bool serverTimestampsAreUtc, Func<string, Task> search,
        IReadOnlySet<SearchSource>? blindSpots = null)
    {
        _record = record;
        _blind = blindSpots ?? new HashSet<SearchSource>();
        Title = record.Title.Length > 0 && MacAddresses.Normalise(record.Title) is { } titleMac ? MacAddresses.Format(titleMac, macFormat) : record.Title;
        Names = record.Names;
        AllLocations = record.Locations.Select(l => new LocationItemViewModel(l, windows, serverTimestampsAreUtc)).ToList();
        Macs = record.Macs.Select(m => new MacItemViewModel(m, macFormat, serverTimestampsAreUtc, search)).ToList();
        Addresses = record.Addresses.Select(a => new CopyableValueViewModel(a, search)).ToList();
        Neighbours = record.Neighbours
            .GroupBy(n => (n.DeviceId, n.PortId))
            .Select(g => new ArpNeighbourViewModel(g.First(), g.Select(n => MacAddresses.Format(n.Mac, macFormat)).Distinct().ToList(), windows))
            .ToList();
        AllSubnetInterfaces = record.Subnet?.Interfaces.Select(i => new SubnetInterfaceViewModel(i, windows, search)).ToList() ?? [];
        ToggleSubnetCommand = new RelayCommand(() => ShowAllSubnet = !ShowAllSubnet);
        ToggleConnectionsCommand = new RelayCommand(() => ShowAllConnections = !ShowAllConnections);
        Conflicts = record.Conflicts.Select(c => Describe(c, record, macFormat)).ToList();

        var device = record.MonitoredDevice;
        OpenDeviceCommand = new RelayCommand(() => windows.ShowDeviceDetail(device!.DeviceId), () => device is not null);
        OpenInterfaceCommand = new RelayCommand(
            () => windows.ShowDevicePort(record.MonitoredInterface!.DeviceId, record.MonitoredInterface.PortId),
            () => record.MonitoredInterface is not null);
        ToggleLocationsCommand = new RelayCommand(() => ShowAllLocations = !ShowAllLocations);
        Best = record.BestLocation is { } best ? AllLocations.First(l => l.Location == best) : null;
    }

    public string Title { get; }

    public IReadOnlyList<string> Names { get; }

    /// <summary>"sw-access-01.example.net" when this address is a monitored device's.</summary>
    public string? MonitoredDeviceName => _record.MonitoredDevice is { } d ? DeviceName(d) : null;

    public bool IsMonitored => _record.MonitoredDevice is not null;

    /// <summary>The matched device in brief, like a Device Details neighbour card - when the endpoint is one.</summary>
    public DeviceCardFacts? Device => _record.MonitoredDevice is { } d ? new DeviceCardFacts(d, DeviceName(d)) : null;

    /// <summary>Addresses joined for the overview card, "192.0.2.1, 2001:db8::1".</summary>
    public string AddressesText => string.Join(", ", _record.Addresses);

    public string? MonitoredInterfaceName => _record.MonitoredInterface?.DisplayName;

    public bool HasMonitoredInterface => _record.MonitoredInterface is not null;

    /// <summary>The port it most likely plugs into - the page's headline.</summary>
    public LocationItemViewModel? Best { get; }

    public bool HasBest => Best is not null;

    /// <summary>"Connected to" only when there's a port to name; otherwise the card says where it was seen.</summary>
    public string ConnectionHeading => IsMonitored || HasAccessLocations ? "Connected to" : "Where it is";

    /// <summary>Why there's no access port to show, for an endpoint LibreNMS doesn't monitor.</summary>
    public bool ShowNoBestText => !IsMonitored && !HasAccessLocations;

    /// <summary>
    /// The one-line answer when there's no clear port: why not, in the terms
    /// the evidence allows.
    /// </summary>
    /// <summary>"Seen on" with nothing in it: a real negative, or a source that couldn't be read.</summary>
    public string NoLocationsText => _blind.Contains(SearchSource.ForwardingTable)
        ? "Switch MAC tables couldn't be checked, so where it's connected isn't known."
        : "No switch's MAC table has it.";

    /// <summary>"Neighbour of" with nothing in it, said as precisely as the coverage allows.</summary>
    public string NoNeighboursText => _blind.Contains(SearchSource.Arp)
        ? "Router ARP tables couldn't be checked."
        : "No router's ARP table has it.";

    public string NoBestText => _blind.Contains(SearchSource.ForwardingTable) && _record.Locations.Count == 0
        ? "Switch MAC tables couldn't be checked, so the port it's connected to isn't known."
        : _record.Locations.Count > 0
        ? "Seen only on links between switches or aggregates, so there's no clear access port. It may sit behind a switch LibreNMS doesn't poll."
        : _record.Neighbours.Count > 0
            ? "Known to a router, but no polled switch's MAC table has it."
            : IsMonitored
                ? "A monitored device. No switch's MAC table has its address."
                : "Not seen in any switch's MAC table.";

    public IReadOnlyList<LocationItemViewModel> AllLocations { get; }

    public IReadOnlyList<LocationItemViewModel> Locations => ShowAllLocations ? AllLocations : AllLocations.Take(LocationsShown).ToList();

    public bool HasLocations => AllLocations.Count > 0;

    public bool ShowAllLocations
    {
        get => _showAllLocations;
        private set
        {
            if (SetProperty(ref _showAllLocations, value))
            {
                OnPropertyChanged(nameof(Locations));
                OnPropertyChanged(nameof(MoreLocationsText));
            }
        }
    }

    public bool HasMoreLocations => AllLocations.Count > LocationsShown;

    public string MoreLocationsText => ShowAllLocations
        ? "Show fewer"
        : $"+{(AllLocations.Count - LocationsShown).ToString(CultureInfo.CurrentCulture)} more";

    public string LocationsSummary => AllLocations.Count switch
    {
        0 => "None",
        1 => "1 port",
        var n => $"{n.ToString(CultureInfo.CurrentCulture)} ports, closest first",
    };

    public IReadOnlyList<MacItemViewModel> Macs { get; }

    public bool HasMacs => Macs.Count > 0;

    public IReadOnlyList<CopyableValueViewModel> Addresses { get; }

    public bool HasAddresses => Addresses.Count > 0;

    public IReadOnlyList<ArpNeighbourViewModel> Neighbours { get; }

    public bool HasNeighbours => Neighbours.Count > 0;

    public string? SubnetNetwork => _record.Subnet?.Network;

    public bool HasSubnet => _record.Subnet is not null;

    public IReadOnlyList<SubnetInterfaceViewModel> AllSubnetInterfaces { get; }

    /// <summary>The gateway and the first few, or all of them once expanded.</summary>
    public IReadOnlyList<SubnetInterfaceViewModel> SubnetInterfaces => ShowAllSubnet ? AllSubnetInterfaces : AllSubnetInterfaces.Take(SubnetInterfacesShown).ToList();

    public bool ShowAllSubnet
    {
        get => _showAllSubnet;
        private set
        {
            if (SetProperty(ref _showAllSubnet, value))
            {
                OnPropertyChanged(nameof(SubnetInterfaces));
                OnPropertyChanged(nameof(MoreSubnetText));
            }
        }
    }

    public bool HasMoreSubnet => AllSubnetInterfaces.Count > SubnetInterfacesShown;

    public string MoreSubnetText => ShowAllSubnet
        ? "Show fewer"
        : $"+{(AllSubnetInterfaces.Count - SubnetInterfacesShown).ToString(CultureInfo.CurrentCulture)} more monitored interfaces on this network";

    /// <summary>Interfaces past what one search reads (a very large network), said rather than hidden.</summary>
    public string? NotLoadedSubnetText => _record.Subnet is { } s && s.TotalInterfaces > s.Interfaces.Count
        ? $"{(s.TotalInterfaces - s.Interfaces.Count).ToString("N0", CultureInfo.CurrentCulture)} more interfaces on this network aren't listed. Search the network to see them all."
        : null;

    public RelayCommand ToggleSubnetCommand { get; }

    // ---------------------------------------------------- Connected to

    /// <summary>
    /// For an endpoint LibreNMS doesn't monitor: every current access port it
    /// was learned on (usually one), never the uplinks it merely passed
    /// through. A host on two access ports - a dual-homed server - shows both.
    /// </summary>
    public IReadOnlyList<LocationItemViewModel> AccessLocations => AllLocations.Where(l => !l.IsStale && !l.IsUplink).ToList();

    public bool HasAccessLocations => AccessLocations.Count > 0;

    /// <summary>
    /// For a monitored device: its neighbours, as Device Details lists them -
    /// every switch, server or access point it links to, uplinks included.
    /// Loaded after the search; null until then.
    /// </summary>
    public IReadOnlyList<DeviceNeighbourItemViewModel>? AllConnections
    {
        get => _allConnections;
        set
        {
            if (SetProperty(ref _allConnections, value))
            {
                OnPropertyChanged(nameof(Connections));
                OnPropertyChanged(nameof(HasConnections));
                OnPropertyChanged(nameof(HasMoreConnections));
                OnPropertyChanged(nameof(MoreConnectionsText));
                OnPropertyChanged(nameof(IsLoadingConnections));
                OnPropertyChanged(nameof(NoConnectionsText));
            }
        }
    }

    /// <summary>Connections shown before "+N more connections".</summary>
    public const int ConnectionsShown = 4;

    public IReadOnlyList<DeviceNeighbourItemViewModel> Connections =>
        AllConnections is null ? [] : ShowAllConnections ? AllConnections : AllConnections.Take(ConnectionsShown).ToList();

    public bool HasConnections => AllConnections is { Count: > 0 };

    public bool IsLoadingConnections => IsMonitored && AllConnections is null && !ConnectionsFailed;

    public bool ConnectionsFailed
    {
        get => _connectionsFailed;
        set
        {
            if (SetProperty(ref _connectionsFailed, value))
            {
                OnPropertyChanged(nameof(IsLoadingConnections));
                OnPropertyChanged(nameof(NoConnectionsText));
            }
        }
    }

    public string? NoConnectionsText => !IsMonitored ? null
        : ConnectionsFailed ? "Its neighbours couldn't be read."
        : AllConnections is { Count: 0 } ? "LibreNMS hasn't discovered any neighbours for this device."
        : null;

    public bool ShowAllConnections
    {
        get => _showAllConnections;
        private set
        {
            if (SetProperty(ref _showAllConnections, value))
            {
                OnPropertyChanged(nameof(Connections));
                OnPropertyChanged(nameof(MoreConnectionsText));
            }
        }
    }

    public bool HasMoreConnections => AllConnections is { } all && all.Count > ConnectionsShown;

    public string MoreConnectionsText => ShowAllConnections || AllConnections is null
        ? "Show fewer"
        : $"+{(AllConnections.Count - ConnectionsShown).ToString(CultureInfo.CurrentCulture)} more connections";

    public RelayCommand ToggleConnectionsCommand { get; }

    /// <summary>The device's id, for loading its neighbours.</summary>
    public int? MonitoredDeviceId => _record.MonitoredDevice?.DeviceId;

    public IReadOnlyList<string> Conflicts { get; }

    public bool HasConflicts => Conflicts.Count > 0;

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenInterfaceCommand { get; }

    public RelayCommand ToggleLocationsCommand { get; }

    internal static string DeviceName(Device? device, int deviceId = 0)
        => device is null ? $"Device {deviceId.ToString(CultureInfo.InvariantCulture)}"
            : !string.IsNullOrWhiteSpace(device.Display) ? device.Display!
            : device.Hostname ?? device.SysName ?? $"Device {device.DeviceId.ToString(CultureInfo.InvariantCulture)}";

    private static string Describe(EndpointConflict conflict, EndpointRecord record, MacAddressFormat format) => conflict.Kind switch
    {
        EndpointConflictKind.AddressWithSeveralMacs =>
            $"{conflict.Subject} answers from {conflict.Values.Count} MACs ({string.Join(", ", conflict.Values.Select(m => MacAddresses.Format(m, format)))}): a duplicate address, a failover pair, or spoofing.",
        _ =>
            $"{MacAddresses.Format(conflict.Subject, format)} is learned on {conflict.Values.Count} access ports at once ({string.Join(", ", conflict.Values.Select(p => PortName(record, p)))}): a loop, a host that moved, or a cloned MAC.",
    };

    private static string PortName(EndpointRecord record, string portId)
        => record.Locations.FirstOrDefault(l => l.PortId.ToString(CultureInfo.InvariantCulture) == portId) is { } l
            ? $"{DeviceName(l.Device, l.DeviceId)} {l.Port?.DisplayName ?? "port " + portId}"
            : "port " + portId;
}

/// <summary>A switch port that learned one of the endpoint's MACs.</summary>
public sealed class LocationItemViewModel
{
    public LocationItemViewModel(EndpointLocation location, IWindowService windows, bool serverTimestampsAreUtc)
    {
        Location = location;
        DeviceName = EndpointViewModel.DeviceName(location.Device, location.DeviceId);
        PortName = location.Port?.DisplayName ?? $"Port {location.PortId.ToString(CultureInfo.InvariantCulture)}";
        PortDescription = location.Port is { IfAlias: { Length: > 0 } alias } port && alias != port.DisplayName ? alias : null;

        var facts = new List<string>
        {
            location.VlanNumber is { } vlan ? $"VLAN {vlan.ToString(CultureInfo.InvariantCulture)}" + (location.VlanName is { } name ? $" ({name})" : string.Empty) : "VLAN unknown",
        };
        if (location.MacCount is { } count)
        {
            facts.Add(count == 1 ? "1 MAC" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} MACs");
        }

        if (PortDescription is not null)
        {
            facts.Add(PortDescription);
        }

        if (location.UpdatedAt is { } updated)
        {
            facts.Add(DurationAgo(ServerTime.Age(updated, serverTimestampsAreUtc)));
            UpdatedToolTip = "Last reported " + ServerTime.ToLocal(updated, serverTimestampsAreUtc).ToString("dd MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        }

        FactsText = string.Join(" · ", facts);

        (KindText, KindTone, KindToolTip) = location.Kind switch
        {
            LocationKind.Direct => ("Direct", AlertSeverity.Ok, "The only MAC this port has learned: the endpoint plugs in here."),
            LocationKind.Shared => ("Shared", AlertSeverity.Warning, "One of several MACs on an access port: behind a hub, phone or small switch."),
            LocationKind.Uplink => ("Uplink", AlertSeverity.Unknown, location.IsAggregate
                ? "A link aggregate: the MAC passes through here on its way somewhere else."
                : "A link to another switch: the MAC passes through here on its way somewhere else."),
            _ => ("Access", AlertSeverity.Unknown, "An access port whose MAC count couldn't be read, so it isn't known whether the endpoint is alone on it."),
        };

        if (location.IsStale)
        {
            KindText += " · stale";
            KindToolTip += " Last seen more than a day before the endpoint's newest sighting.";
        }

        OpenDeviceCommand = new RelayCommand(() => windows.ShowDeviceDetail(location.DeviceId));
        OpenPortCommand = new RelayCommand(() => windows.ShowDevicePort(location.DeviceId, location.PortId));
        AccessibleText = $"{DeviceName}, {PortName}, {KindText}, {FactsText}";

        Switch = new DeviceCardFacts(location.Device, DeviceName);
        Port = location.Port is { } p ? new PortItemViewModel(p, null, [], _ => { }) : null;
        VlanText = location.VlanNumber is { } number
            ? $"VLAN {number.ToString(CultureInfo.InvariantCulture)}" + (location.VlanName is { } vlanName ? $" ({vlanName})" : string.Empty)
            : "VLAN unknown";
        MacCountText = location.MacCount switch
        {
            null => null,
            1 => "Only MAC on this port",
            var n => $"{n.Value.ToString("N0", CultureInfo.CurrentCulture)} MACs on this port",
        };
        SeenText = location.UpdatedAt is { } at ? "Seen " + DurationAgo(ServerTime.Age(at, serverTimestampsAreUtc)) : null;
    }

    /// <summary>The switch, as Device Details' neighbour cards show a device: its state and what it is.</summary>
    public DeviceCardFacts Switch { get; }

    /// <summary>The port's own status, speed and traffic, as on the Ports tab - null when the port couldn't be read.</summary>
    public PortItemViewModel? Port { get; }

    /// <summary>Only when the port's status is known - a port read without it would only say "Unknown".</summary>
    public bool HasPort => Location.Port?.IfOperStatus is { Length: > 0 };

    /// <summary>"Up · 1 Gbps", the Neighbours card's port line.</summary>
    public string PortStateText => Port is null ? string.Empty : $"{Port.StatusText} · {Port.SpeedText}";

    public string VlanText { get; }

    public string? MacCountText { get; }

    public bool HasMacCount => MacCountText is not null;

    public string? SeenText { get; }

    public bool HasSeen => SeenText is not null;

    public EndpointLocation Location { get; }

    public string DeviceName { get; }

    public string PortName { get; }

    public string? PortDescription { get; }

    public string FactsText { get; }

    public string KindText { get; }

    public AlertSeverity KindTone { get; }

    public string KindToolTip { get; }

    public string? UpdatedToolTip { get; }

    public bool IsUplink => Location.Kind == LocationKind.Uplink;

    public bool IsStale => Location.IsStale;

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenPortCommand { get; }

    public string AccessibleText { get; }

    internal static string DurationAgo(TimeSpan span)
        => span < TimeSpan.FromMinutes(1) ? "just now"
            : span.TotalDays >= 1 ? $"{(int)span.TotalDays}d ago"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalMinutes}m ago";
}

/// <summary>
/// A device as Device Details' neighbour cards show one: a state tile, its
/// name, "hardware · type · IP" and its state in words.
/// </summary>
public sealed class DeviceCardFacts
{
    public DeviceCardFacts(Device? device, string name)
    {
        Name = name;
        Severity = device?.State switch
        {
            DeviceState.Up => AlertSeverity.Ok,
            DeviceState.Down => AlertSeverity.Critical,
            DeviceState.Maintenance => AlertSeverity.Warning,
            _ => AlertSeverity.Unknown,
        };
        StateText = device is null ? "Not visible" : device.State.ToDisplayString();
        var parts = device is null ? [] : new[] { device.Hardware, TitleCase(device.Type), device.Ip };
        Description = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        Location = string.IsNullOrWhiteSpace(device?.Location) ? null : device!.Location;
        Version = device is null ? null : string.Join(" ", new[] { device.Os, device.Version }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public string Name { get; }

    public AlertSeverity Severity { get; }

    public string StateText { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public string? Location { get; }

    public bool HasLocation => Location is not null;

    public string? Version { get; }

    public bool HasVersion => !string.IsNullOrEmpty(Version);

    private static string? TitleCase(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>A value with a copy button - an address, or anything else worth pasting elsewhere.</summary>
public sealed class CopyableValueViewModel
{
    public CopyableValueViewModel(string value, Func<string, Task>? search = null)
    {
        Value = value;
        CopyCommand = new RelayCommand(() => UiPlatform.Clipboard.SetText(Value));
        SearchCommand = new AsyncRelayCommand(() => search?.Invoke(Value) ?? Task.CompletedTask, () => search is not null);
    }

    public string Value { get; }

    public RelayCommand CopyCommand { get; }

    /// <summary>Searches for this value - an address on another row, a gateway.</summary>
    public AsyncRelayCommand SearchCommand { get; }

    public string CopyName => "Copy " + Value;
}

/// <summary>One of the endpoint's MACs.</summary>
public sealed class MacItemViewModel
{
    public MacItemViewModel(EndpointMac mac, MacAddressFormat format, bool serverTimestampsAreUtc, Func<string, Task> search)
    {
        Value = new CopyableValueViewModel(MacAddresses.Format(mac.Hex, format), search);
        Vendor = mac.Vendor;
        var seen = mac.LastSeen is { } t ? "last seen " + LocationItemViewModel.DurationAgo(ServerTime.Age(t, serverTimestampsAreUtc)) : null;
        DetailText = string.Join(" · ", new[] { seen, mac.IsStale ? "stale" : null }.OfType<string>());
        IsStale = mac.IsStale;
    }

    public CopyableValueViewModel Value { get; }

    public string? Vendor { get; }

    public bool HasVendor => Vendor is not null;

    public string DetailText { get; }

    public bool HasDetail => DetailText.Length > 0;

    public bool IsStale { get; }
}

/// <summary>A router interface whose ARP table holds the endpoint.</summary>
public sealed class ArpNeighbourViewModel
{
    public ArpNeighbourViewModel(EndpointNeighbour neighbour, IReadOnlyList<string> macs, IWindowService windows)
    {
        DeviceName = EndpointViewModel.DeviceName(neighbour.Device, neighbour.DeviceId);
        PortName = neighbour.Port?.DisplayName;
        Source = neighbour.Source;
        MacsText = string.Join(", ", macs);
        OpenDeviceCommand = new RelayCommand(() => windows.ShowDeviceDetail(neighbour.DeviceId));
        OpenPortCommand = new RelayCommand(() => windows.ShowDevicePort(neighbour.DeviceId, neighbour.PortId), () => neighbour.PortId > 0);
    }

    public string DeviceName { get; }

    public string? PortName { get; }

    public bool HasPortName => PortName is not null;

    public string Source { get; }

    public string MacsText { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenPortCommand { get; }
}

/// <summary>A monitored interface on the endpoint's subnet - usually its gateway.</summary>
public sealed class SubnetInterfaceViewModel
{
    public SubnetInterfaceViewModel(SubnetInterface item, IWindowService windows, Func<string, Task> search)
    {
        DeviceName = item.Device is null && item.Port is null ? "Unnamed interface" : EndpointViewModel.DeviceName(item.Device, item.Port?.DeviceId ?? 0);
        PortName = item.Port?.DisplayName;
        AddressText = item.PrefixLength is { } p ? $"{item.Address}/{p.ToString(CultureInfo.InvariantCulture)}" : item.Address;
        Address = new CopyableValueViewModel(item.Address, search);
        var deviceId = item.Port?.DeviceId ?? 0;
        OpenPortCommand = new RelayCommand(() => windows.ShowDevicePort(deviceId, item.PortId), () => deviceId > 0);
        OpenDeviceCommand = new RelayCommand(() => windows.ShowDeviceDetail(deviceId), () => deviceId > 0);
    }

    public string DeviceName { get; }

    public string? PortName { get; }

    public bool HasPortName => PortName is not null;

    public string AddressText { get; }

    public CopyableValueViewModel Address { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenPortCommand { get; }
}

/// <summary>One row of a list result: what ARP and the device list said, with a way to open it in full.</summary>
public sealed class EndpointRowViewModel
{
    public EndpointRowViewModel(EndpointRecord record, MacAddressFormat format, IWindowService windows, Func<string, Task> search)
    {
        Record = record;
        AddressText = record.Addresses.Count == 0 ? "—" : string.Join(", ", record.Addresses);
        MacText = record.Macs.Count == 0 ? "—" : string.Join(", ", record.Macs.Select(m => MacAddresses.Format(m.Hex, format)));
        NameText = record.Names.FirstOrDefault() ?? string.Empty;
        NeighbourText = string.Join(", ", record.Neighbours
            .Select(n => EndpointViewModel.DeviceName(n.Device, n.DeviceId))
            .Distinct());
        HasConflict = record.Conflicts.Count > 0;
        ConflictToolTip = HasConflict ? "This address answers from more than one MAC" : null;

        // Opening a row searches its most specific key: its address, else its MAC, else its name.
        var key = record.Addresses.FirstOrDefault() ?? record.Macs.FirstOrDefault()?.Hex ?? record.Names.FirstOrDefault() ?? record.Title;
        OpenCommand = new AsyncRelayCommand(() => search(key));
        OpenName = "See where " + (MacAddresses.Normalise(key) is { } keyMac ? MacAddresses.Format(keyMac, format) : key) + " is connected";
        var device = record.MonitoredDevice;
        OpenDeviceCommand = new RelayCommand(() => windows.ShowDeviceDetail(device!.DeviceId), () => device is not null);
        SortKey = record.Addresses.FirstOrDefault() is { } address && System.Net.IPAddress.TryParse(address, out var ip)
            ? Convert.ToHexString(ip.GetAddressBytes()).PadLeft(32, '0')
            : "~" + record.Title;

        // What the filters test, LibreNMS's Source facet and who knows it.
        var sources = new List<string>();
        if (record.Neighbours.Count > 0)
        {
            sources.Add(SourceNeighbourTables);
        }

        if (record.Locations.Count > 0)
        {
            sources.Add(SourceForwardingTables);
        }

        if (record.MonitoredInterface is not null || record.MonitoredInterfacePortId is not null)
        {
            sources.Add(SourceInterfaceAddresses);
        }

        if (record.MonitoredDevice is not null)
        {
            sources.Add(SourceDevices);
        }

        SourceKeys = sources;
        SeenByKeys = record.Neighbours.Select(n => EndpointViewModel.DeviceName(n.Device, n.DeviceId)).Distinct().ToList();
        MonitoredKey = record.IsMonitored ? "Monitored" : "Not monitored";
    }

    /// <summary>The Seen by value for a row no router's ARP table holds, so Select none can hide those too.</summary>
    public const string NoRouter = "No router";

    public EndpointRecord Record { get; }

    /// <summary>The open button's spoken name - meaningful even for a row with no address.</summary>
    public string OpenName { get; }

    public const string SourceNeighbourTables = "Neighbour tables (ARP)";
    public const string SourceForwardingTables = "Forwarding tables (FDB)";
    public const string SourceInterfaceAddresses = "Interface addresses";
    public const string SourceDevices = "Devices";

    /// <summary>Where this row was found - the Source filter's values.</summary>
    public IReadOnlyList<string> SourceKeys { get; }

    /// <summary>The routers whose ARP tables hold it - the Seen by filter's values.</summary>
    public IReadOnlyList<string> SeenByKeys { get; }

    /// <summary>"Monitored" or "Not monitored".</summary>
    public string MonitoredKey { get; }

    public string AddressText { get; }

    public string MacText { get; }

    public string NameText { get; }

    public bool HasName => NameText.Length > 0;

    public string NeighbourText { get; }

    public bool HasConflict { get; }

    public string? ConflictToolTip { get; }

    /// <summary>Sorts addresses numerically rather than as text.</summary>
    public string SortKey { get; }

    public AsyncRelayCommand OpenCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public bool Matches(string filter) => string.IsNullOrWhiteSpace(filter)
        || AddressText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || MacText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || NameText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || NeighbourText.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
