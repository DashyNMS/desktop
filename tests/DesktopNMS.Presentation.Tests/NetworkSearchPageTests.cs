using DesktopNMS.Core.Models;
using DesktopNMS.Core.Search;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Network Search page's view models (#57): the answer leads, uplinks are
/// context, MACs follow the chosen format, conflicts and blind spots are said
/// in plain words. Synthetic data only.
/// </summary>
public sealed class NetworkSearchPageTests
{
    private static readonly Device Switch = new() { DeviceId = 1, Hostname = "sw-access-01.example.net", Display = "sw-access-01" };
    private static readonly Device Core = new() { DeviceId = 2, Hostname = "sw-core-01.example.net" };
    private static readonly Device Router = new() { DeviceId = 3, Hostname = "fw-01.example.net" };

    private static EndpointLocation Location(Device device, int portId, string port, int? macs, bool uplink = false, int? vlan = 2069)
        => new("00005e005368", device.DeviceId, portId, device, new Port { PortId = portId, DeviceId = device.DeviceId, IfName = port }, vlan, null, macs, uplink, false, DateTime.UtcNow.AddMinutes(-5), false);

    private static EndpointRecord Record(IReadOnlyList<EndpointLocation>? locations = null, IReadOnlyList<EndpointConflict>? conflicts = null) => new()
    {
        Title = "192.0.2.117",
        Addresses = ["192.0.2.117"],
        Macs = [new EndpointMac("00005e005368", "ICANN, IANA Department", DateTime.UtcNow, false)],
        Names = [],
        Locations = locations ?? [Location(Switch, 19, "19", 1), Location(Core, 37, "Eth1/37", 5, uplink: true)],
        Neighbours = [new EndpointNeighbour("ARP", "192.0.2.117", "00005e005368", Router.DeviceId, 69, Router, new Port { PortId = 69, DeviceId = 3, IfName = "lagg1.2069" })],
        Conflicts = conflicts ?? [],
        IsDetailed = true,
    };

    private static EndpointViewModel Page(EndpointRecord record, MacAddressFormat format = MacAddressFormat.Colon)
        => new(record, format, new NullWindows(), serverTimestampsAreUtc: true, _ => Task.CompletedTask);

    [Fact]
    public void The_answer_is_the_access_port_not_the_uplink()
    {
        var page = Page(Record());

        Assert.True(page.HasBest);
        Assert.Equal("Connected to", page.ConnectionHeading);
        Assert.Equal("sw-access-01", page.Best!.DeviceName);
        Assert.Equal("19", page.Best.PortName);
        Assert.Equal("Direct", page.Best.KindText);
        Assert.StartsWith("VLAN 2069 · 1 MAC", page.Best.FactsText, StringComparison.Ordinal);
        Assert.Equal("Uplink", page.AllLocations[1].KindText);
        Assert.True(page.AllLocations[1].IsUplink);
    }

    [Fact]
    public void Only_uplinks_fall_back_to_the_closest_sighting_and_say_so()
    {
        var page = Page(Record([Location(Core, 37, "Eth1/37", 5, uplink: true)]));

        Assert.False(page.HasBest);
        Assert.Equal("Connected to", page.ConnectionHeading);
        Assert.Equal("Uplink", Assert.Single(page.MacLocations).KindText);
        Assert.Contains("links between switches", page.MacLocationsNote, StringComparison.Ordinal);
        Assert.False(page.ShowNoBestText);
    }

    [Fact]
    public void A_device_with_no_neighbours_falls_back_to_where_its_mac_is_learned()
    {
        var record = new EndpointRecord
        {
            Title = "192.0.2.20", Addresses = ["192.0.2.20"], Macs = [], Names = ["sw-access-01"], MonitoredDevice = Switch,
            Locations = [Location(Core, 37, "Eth1/37", 1)], Neighbours = [], Conflicts = [],
        };
        var page = Page(record);
        Assert.Empty(page.MacLocations); // not until its neighbours are known to be missing

        page.AllConnections = [];

        Assert.Equal("Direct", Assert.Single(page.MacLocations).KindText);
        Assert.Equal("Connected to", page.ConnectionHeading);
        Assert.Null(page.NoConnectionsText);
        Assert.Contains("No discovered neighbours", page.MacLocationsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_vlan_and_mac_count_are_said_not_guessed()
    {
        var page = Page(Record([Location(Switch, 19, "19", macs: null, vlan: null)]));

        var only = Assert.Single(page.AllLocations);
        Assert.Equal("Access", only.KindText);
        Assert.StartsWith("VLAN unknown", only.FactsText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MacAddressFormat.Colon, "00:00:5e:00:53:68")]
    [InlineData(MacAddressFormat.Dotted, "0000.5e00.5368")]
    [InlineData(MacAddressFormat.Hyphen, "00-00-5e-00-53-68")]
    public void Macs_follow_the_chosen_format(MacAddressFormat format, string expected)
    {
        var page = Page(Record(), format);

        Assert.Equal(expected, Assert.Single(page.Macs).Value.Value);
        Assert.Equal(expected, Assert.Single(page.Neighbours).MacsText);
    }

    [Fact]
    public void Five_ports_show_then_more()
    {
        var many = Enumerable.Range(1, 8).Select(i => Location(Core, 100 + i, $"Eth1/{i}", 10 + i, uplink: true)).ToList();
        var page = Page(Record(many));

        Assert.Equal(EndpointViewModel.LocationsShown, page.Locations.Count);
        Assert.Equal("+3 more", page.MoreLocationsText);
        page.ToggleLocationsCommand.Execute(null);
        Assert.Equal(8, page.Locations.Count);
        Assert.Equal("Show fewer", page.MoreLocationsText);
    }

    [Fact]
    public void A_conflict_names_the_macs_in_the_chosen_format()
    {
        var conflict = new EndpointConflict(EndpointConflictKind.AddressWithSeveralMacs, "192.0.2.200", ["00005e005301", "00005e005302"]);
        var page = Page(Record(conflicts: [conflict]), MacAddressFormat.Dotted);

        var text = Assert.Single(page.Conflicts);
        Assert.Contains("0000.5e00.5301", text, StringComparison.Ordinal);
        Assert.Contains("duplicate address", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SourceState.PermissionDenied, true, "no permission")]
    [InlineData(SourceState.Unavailable, true, "unavailable")]
    [InlineData(SourceState.Failed, true, "failed")]
    [InlineData(SourceState.NoEntry, false, "nothing")]
    [InlineData(SourceState.Found, false, "found")]
    public void Coverage_names_blind_spots(SourceState state, bool blind, string text)
    {
        var item = new CoverageItemViewModel(new SourceCoverage(SearchSource.ForwardingTable, state));

        Assert.Equal(blind, item.IsBlindSpot);
        Assert.Equal(text, item.StateText);
        Assert.Equal("Switch MAC tables", item.Label);
    }

    [Fact]
    public void A_truncated_source_is_a_blind_spot_with_its_counts()
    {
        var item = new CoverageItemViewModel(new SourceCoverage(SearchSource.ForwardingTable, SourceState.Found, Shown: 8, Total: 12));

        Assert.True(item.IsBlindSpot);
        Assert.Equal("found, 8 of 12 checked", item.StateText);
    }

    [Fact]
    public void A_list_row_opens_by_its_address_and_filters_on_any_text()
    {
        string? opened = null;
        var row = new EndpointRowViewModel(Record(), MacAddressFormat.Colon, new NullWindows(), q => { opened = q; return Task.CompletedTask; });

        row.OpenCommand.Execute(null);

        Assert.Equal("192.0.2.117", opened);
        Assert.True(row.Matches("fw-01"));
        Assert.True(row.Matches("53:68"));
        Assert.False(row.Matches("198.51.100"));
    }

    [Fact]
    public void Rows_carry_the_values_the_filters_test()
    {
        var record = Record();
        var row = new EndpointRowViewModel(record, MacAddressFormat.Colon, new NullWindows(), _ => Task.CompletedTask);

        Assert.Equal([EndpointRowViewModel.SourceNeighbourTables, EndpointRowViewModel.SourceForwardingTables], row.SourceKeys);
        Assert.Equal(["fw-01.example.net"], row.SeenByKeys);
        Assert.Equal("Not monitored", row.MonitoredKey);

        var facet = new FilterFacet(() => { });
        facet.Apply([(EndpointRowViewModel.SourceNeighbourTables, EndpointRowViewModel.SourceNeighbourTables, 1), (EndpointRowViewModel.SourceDevices, EndpointRowViewModel.SourceDevices, 1)]);
        facet.Options.Single(o => o.Key == EndpointRowViewModel.SourceNeighbourTables).IsChecked = false;
        Assert.True(facet.AllowsAny(row.SourceKeys)); // still found in a forwarding table, which isn't filtered out
    }

    [Fact]
    public void The_connected_card_reads_like_a_device_details_neighbour_card()
    {
        var switchDevice = new Device { DeviceId = 1, Hostname = "sw-access-01.example.net", Hardware = "C9300-48P", Type = "network", Ip = "192.0.2.20", Status = true };
        var port = new Port { PortId = 19, DeviceId = 1, IfName = "19", IfAlias = "Lobby display", IfOperStatus = "up", IfSpeed = 1_000_000_000 };
        var location = new EndpointLocation("00005e005368", 1, 19, switchDevice, port, 2069, "Displays", 1, false, false, DateTime.UtcNow, false);

        var page = Page(Record([location]));

        Assert.Equal("C9300-48P · Network · 192.0.2.20", page.Best!.Switch.Description);
        Assert.Equal("VLAN 2069 (Displays)", page.Best.VlanText);
        Assert.Equal("Only MAC on this port", page.Best.MacCountText);
        Assert.True(page.Best.HasPort);
        Assert.StartsWith("Up · ", page.Best.PortStateText, StringComparison.Ordinal);
        Assert.Equal("Lobby display", page.Best.PortDescription);
    }

    [Fact]
    public void An_unmonitored_endpoint_on_two_access_ports_shows_both_and_never_an_uplink()
    {
        var page = Page(Record([Location(Switch, 19, "19", 1), Location(Switch, 20, "20", 1), Location(Core, 37, "Eth1/37", 5, uplink: true)]));

        Assert.Equal(["19", "20"], page.AccessLocations.Select(l => l.PortName));
        Assert.All(page.AccessLocations, l => Assert.Equal("Direct", l.KindText));
        Assert.Equal("Connected to", page.ConnectionHeading);
        Assert.False(page.ShowNoBestText);
    }

    [Fact]
    public void A_monitored_device_waits_for_its_neighbours_then_shows_four_and_more()
    {
        var record = new EndpointRecord
        {
            Title = "192.0.2.20", Addresses = ["192.0.2.20"], Macs = [], Names = ["sw-access-01"], MonitoredDevice = Switch,
            Locations = [], Neighbours = [], Conflicts = [],
        };
        var page = Page(record);
        Assert.True(page.IsLoadingConnections);

        page.AllConnections = Enumerable.Range(1, 6)
            .Select(i => new DeviceNeighbourItemViewModel(
                new DesktopNMS.Core.Topology.DeviceNeighbour(DesktopNMS.Core.Topology.NeighbourSource.ThisDevice, i, $"Gi1/0/{i}", $"ap-{i}.example.net", null, "bond0", null, DesktopNMS.Core.Topology.NeighbourMatchKind.None, ["lldp"], null, true),
                null, null, null, "sw-access-01", _ => { }))
            .ToList();

        Assert.False(page.IsLoadingConnections);
        Assert.Equal(EndpointViewModel.ConnectionsShown, page.Connections.Count);
        Assert.Equal("+2 more connections", page.MoreConnectionsText);
        page.ToggleConnectionsCommand.Execute(null);
        Assert.Equal(6, page.Connections.Count);

        page.AllConnections = [];
        Assert.Equal("LibreNMS hasn't discovered any neighbours for this device.", page.NoConnectionsText);
    }

    [Fact]
    public void The_subnet_expands_to_every_interface()
    {
        var interfaces = Enumerable.Range(1, 9).Select(i => new SubnetInterface($"192.0.2.{i}", 24, i, null, null)).ToList();
        var record = Record() is var r ? new EndpointRecord
        {
            Title = r.Title, Addresses = r.Addresses, Macs = r.Macs, Names = r.Names, Locations = r.Locations, Neighbours = r.Neighbours, Conflicts = r.Conflicts,
            Subnet = new EndpointSubnet("192.0.2.0/24", interfaces, 9),
        } : null!;
        var page = Page(record);

        Assert.Equal(EndpointViewModel.SubnetInterfacesShown, page.SubnetInterfaces.Count);
        Assert.Equal("+4 more monitored interfaces on this network", page.MoreSubnetText);
        page.ToggleSubnetCommand.Execute(null);
        Assert.Equal(9, page.SubnetInterfaces.Count);
        Assert.Null(page.NotLoadedSubnetText);
    }

    /// <summary>Records nothing: these tests don't open windows.</summary>
    private sealed class NullWindows : DesktopNMS.Services.IWindowService
    {
        private static T Never<T>() => throw new NotSupportedException();
#pragma warning disable CS0067
        public void ShowMain() { }
        public void HideMain() { }
        public void ShowDevicesTab() { }
        public void ShowAlertsForDevice(int deviceId, string deviceName) { }
        public void ShowDeviceDetail(int deviceId) { }
        public void ShowDeviceGraph(int deviceId, string graphName) { }
        public void ShowDevicePortGraph(int deviceId, string ifName, string graphType) { }
        public void ShowDeviceWireless(int deviceId) { }
        public void ShowDevicePort(int deviceId, int portId) { }
        public void ShowDeviceEventLog(int deviceId, int? eventId = null) { }
        public void ShowDeviceAlerts(int deviceId, int? alertLogId = null) { }
        public void AddPortGraphToDashboard(int deviceId, string ifName, string graphType, string title) { }
        public void ShowDeviceGraylog(int deviceId, GraylogMessage? message = null) { }
        public void ShowGraylogMessage(GraylogMessage message) { }
        public IReadOnlyList<WidgetCatalogEntry> ShowWidgetPicker(IReadOnlyList<WidgetCatalogEntry> catalog) => Never<IReadOnlyList<WidgetCatalogEntry>>();
        public bool ShowWhatsNew() => false;
        public void ShowLicences() { }
        public void ShowNeighbour(string viewId, string? name, string? mac = null) { }
        public DesktopNMS.Core.Configuration.NeighbourViewDefinition? ShowNeighbourViewEditor(DesktopNMS.Core.Configuration.NeighbourViewDefinition? existing) => null;
        public void CloseDeviceDetail(int deviceId) { }
        public void ShowDevicesFilteredByLocation(string location) { }
        public void ShowDevicesFilteredByLocations(IReadOnlyCollection<string> locations) { }
        public void ShowDevicesFilteredByGroup(string groupName) { }
        public void ShowDevicesWithState(DeviceState state) { }
        public void ShowAlertsWithSeverity(AlertSeverity? severity) { }
        public bool ShowSettingsDialog(SettingsSection? section = null) => false;
        public void ShowMainTab(MainTab tab) { }
        public void ShowSettingsSection(SettingsSection section) { }
        public void ShowDeviceFiltersDialog() { }
        public void ShowNetworkSearchFiltersDialog() { }
        public void ShowAlertFiltersDialog() { }
        public bool ShowSignInDialog() => false;
        public bool ShowAddDeviceDialog() => false;
        public bool ShowBulkAddDevicesDialog() => false;
        public bool ShowAddDeviceGroupDialog() => false;
        public bool ShowEditDeviceGroupDialog(DeviceGroup group) => false;
        public bool ShowAddDevicesToGroupDialog(IReadOnlyList<int> deviceIds) => false;
        public bool ShowAddLocationDialog() => false;
        public bool ShowEditLocationDialog(Location location) => false;
        public bool ShowAddRuleDialog() => false;
        public bool ShowEditRuleDialog(AlertRule rule) => false;
        public bool ShowDuplicateRuleDialog(AlertRule rule) => false;
        public string? ShowScheduleMaintenanceDialog(int deviceId, string deviceName) => null;
        public string? ShowScheduleMaintenanceDialog(IReadOnlyList<MaintenanceTarget> devices) => null;
        public bool ShowAddAlertTemplateDialog() => false;
        public bool ShowEditAlertTemplateDialog(AlertTemplate template) => false;
        public void OpenUrl(Uri url) { }
        public void OpenExternalTool(Uri uri) { }
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
        public bool Confirm(string title, string message, string confirmLabel, bool destructive = false) => false;
        public bool ConfirmTrustCertificate(string service, DesktopNMS.Core.Security.CertificateDetails certificate) => false;
        public (bool Confirmed, bool DontAskAgain) ConfirmWithOptOut(string title, string message, string confirmLabel, string dontAskAgainLabel = "Don't ask me again") => (false, false);
        public void Exit() { }
#pragma warning restore CS0067
    }
}
