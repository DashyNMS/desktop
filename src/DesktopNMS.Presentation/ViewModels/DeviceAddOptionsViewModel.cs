using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Everything about how a device is added except its hostname - SNMP or
/// ping-only, credentials, port, poller group, location - shared by the
/// single "Add device" dialog (<see cref="AddDeviceViewModel"/>) and "Bulk
/// add devices" (<see cref="BulkAddDevicesViewModel"/>, where it's the
/// settings every listed device gets unless its CSV row says otherwise).
/// See <see cref="Views.DeviceAddOptionsView"/>.
/// </summary>
public sealed class DeviceAddOptionsViewModel : ObservableObject
{
    /// <summary>Always present regardless of what (if anything) the server returns - represents "don't set poller_group at all", which LibreNMS itself defaults to group 0.</summary>
    private static readonly PollerGroup DefaultPollerGroup = new() { Id = 0, GroupName = "Default (poller 0)" };

    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;

    private bool _isPingOnly;
    private bool _isSnmpV2c = true;
    private bool _isSnmpV1;
    private bool _isSnmpV3;
    private string _community = "public";
    private string _authLevel = "authPriv";
    private string _authName = string.Empty;
    private string _authPass = string.Empty;
    private string _authAlgo = "SHA";
    private string _cryptoPass = string.Empty;
    private string _cryptoAlgo = "AES";
    private string _port = string.Empty;
    private string _transport = string.Empty;
    private string _sysName = string.Empty;
    private string _hardware = string.Empty;
    private string _os = "ping";
    private string _location = string.Empty;
    private PollerGroup _selectedPollerGroup = DefaultPollerGroup;
    private bool _forceAdd;
    private bool _pingFallback = true;

    public DeviceAddOptionsViewModel(ILibreNmsClient client, ILogger logger, bool isBulk)
    {
        _client = client;
        _logger = logger;
        IsBulk = isBulk;

        PollerGroups = new ObservableCollection<PollerGroup> { DefaultPollerGroup };
        KnownOperatingSystems = new ObservableCollection<string> { "ping" };
        KnownLocations = new ObservableCollection<string>();

        _ = LoadPollerGroupsAsync();
        _ = LoadKnownOperatingSystemsAsync();
        _ = LoadKnownLocationsAsync();
    }

    /// <summary>Bulk add: sysName and hardware are per device, so the shared ping-only settings leave them to the CSV.</summary>
    public bool IsBulk { get; }

    /// <summary>
    /// Always has at least <see cref="DefaultPollerGroup"/>; whatever else
    /// LibreNMS reports gets appended once <see cref="LoadPollerGroupsAsync"/>
    /// completes. A single-poller instance with none configured just leaves
    /// this at one entry, which is the correct thing to show either way.
    /// </summary>
    public ObservableCollection<PollerGroup> PollerGroups { get; }

    /// <summary>
    /// Suggestions for the ping-only "OS" field (see <see cref="Os"/>), drawn
    /// from whatever OS short names are already in use on this instance's
    /// fleet - there is no dedicated "list valid OS names" endpoint in the
    /// versioned LibreNMS API, but the device list already has to know them.
    /// Always seeded with "ping", LibreNMS's own default when the field is
    /// left unset, regardless of whether any device already uses it.
    /// </summary>
    public ObservableCollection<string> KnownOperatingSystems { get; }

    /// <summary>Existing location names, as suggestions for <see cref="Location"/> - any other name creates a new location.</summary>
    public ObservableCollection<string> KnownLocations { get; }

    private async Task LoadPollerGroupsAsync()
    {
        try
        {
            var groups = await _client.PollerGroups.ListAsync().ConfigureAwait(true);

            foreach (var group in groups)
            {
                // Skip a real id-0 row rather than showing two "poller 0"
                // entries side by side - LibreNMS itself treats 0 as the
                // implicit default regardless of whether a row exists for it.
                if (group.Id != 0)
                {
                    PollerGroups.Add(group);
                }
            }
        }
        catch (LibreNmsApiException ex)
        {
            // Best-effort: the dropdown just falls back to the one synthetic
            // "Default" entry - still enough to add a device, on a server
            // where this endpoint is unavailable or unauthorized.
            _logger.LogWarning(ex, "Could not load poller groups");
        }
    }

    private async Task LoadKnownOperatingSystemsAsync()
    {
        try
        {
            var devices = await _client.Devices.ListAsync().ConfigureAwait(true);

            var distinctOperatingSystems = devices
                .Select(d => d.Os)
                .Where(os => !string.IsNullOrWhiteSpace(os))
                .Select(os => os!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(os => !os.Equals("ping", StringComparison.OrdinalIgnoreCase))
                .OrderBy(os => os, StringComparer.OrdinalIgnoreCase);

            foreach (var os in distinctOperatingSystems)
            {
                KnownOperatingSystems.Add(os);
            }
        }
        catch (LibreNmsApiException ex)
        {
            // Best-effort: the field is still free text either way, this just
            // loses the autocomplete suggestions from the existing fleet.
            _logger.LogWarning(ex, "Could not load the known OS list from the device fleet");
        }
    }

    private async Task LoadKnownLocationsAsync()
    {
        try
        {
            var locations = await _client.Locations.ListAsync().ConfigureAwait(true);

            foreach (var name in locations
                         .Select(l => l.Name?.Trim())
                         .Where(n => !string.IsNullOrEmpty(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                KnownLocations.Add(name!);
            }
        }
        catch (LibreNmsApiException ex)
        {
            // Best-effort, like the OS suggestions above.
            _logger.LogWarning(ex, "Could not load locations for the add device suggestions");
        }
    }

    // ------------------------------------------------------------ SNMP y/n

    /// <summary>
    /// Top-level choice, separate from which SNMP version - flipping this to
    /// "No" swaps the whole middle of the form over to the ping-only fields
    /// (<see cref="ShowPingOnlyFields"/>) instead of tucking a fourth option
    /// into the version picker, since "no SNMP at all" is a different kind of
    /// choice to "which SNMP version" and deserves its own toggle.
    /// </summary>
    public bool IsSnmpEnabled
    {
        get => !_isPingOnly;
        set { if (value) SetPingOnly(false); }
    }

    public bool IsPingOnly
    {
        get => _isPingOnly;
        set { if (value) SetPingOnly(true); }
    }

    public bool ShowSnmpFields => IsSnmpEnabled;

    public bool ShowPingOnlyFields => IsPingOnly;

    /// <summary>sysName and hardware describe one device, so bulk add leaves them to each CSV row.</summary>
    public bool ShowPerDevicePingOnlyFields => IsPingOnly && !IsBulk;

    private void SetPingOnly(bool pingOnly)
    {
        if (_isPingOnly == pingOnly)
        {
            return;
        }

        _isPingOnly = pingOnly;
        OnPropertyChanged(nameof(IsSnmpEnabled));
        OnPropertyChanged(nameof(IsPingOnly));
        OnPropertyChanged(nameof(ShowSnmpFields));
        OnPropertyChanged(nameof(ShowPingOnlyFields));
        OnPropertyChanged(nameof(ShowPerDevicePingOnlyFields));
    }

    // ---------------------------------------------------------- SNMP version

    public bool IsSnmpV2c
    {
        get => _isSnmpV2c;
        set { if (value) SetSnmpVersion(v2c: true); }
    }

    public bool IsSnmpV1
    {
        get => _isSnmpV1;
        set { if (value) SetSnmpVersion(v1: true); }
    }

    public bool IsSnmpV3
    {
        get => _isSnmpV3;
        set { if (value) SetSnmpVersion(v3: true); }
    }

    /// <summary>Shows the community field - v1 and v2c both use it.</summary>
    public bool ShowCommunity => IsSnmpV1 || IsSnmpV2c;

    public bool ShowV3Fields => IsSnmpV3;

    private void SetSnmpVersion(bool v2c = false, bool v1 = false, bool v3 = false)
    {
        _isSnmpV2c = v2c;
        _isSnmpV1 = v1;
        _isSnmpV3 = v3;

        OnPropertyChanged(nameof(IsSnmpV2c));
        OnPropertyChanged(nameof(IsSnmpV1));
        OnPropertyChanged(nameof(IsSnmpV3));
        OnPropertyChanged(nameof(ShowCommunity));
        OnPropertyChanged(nameof(ShowV3Fields));
    }

    // ----------------------------------------------------------- credentials

    public string Community
    {
        get => _community;
        set => SetProperty(ref _community, value);
    }

    /// <summary>noAuthNoPriv, authNoPriv or authPriv.</summary>
    public string AuthLevel
    {
        get => _authLevel;
        set => SetProperty(ref _authLevel, value);
    }

    public string AuthName
    {
        get => _authName;
        set => SetProperty(ref _authName, value);
    }

    public string AuthPass
    {
        get => _authPass;
        set => SetProperty(ref _authPass, value);
    }

    /// <summary>MD5 or SHA.</summary>
    public string AuthAlgo
    {
        get => _authAlgo;
        set => SetProperty(ref _authAlgo, value);
    }

    public string CryptoPass
    {
        get => _cryptoPass;
        set => SetProperty(ref _cryptoPass, value);
    }

    /// <summary>AES or DES.</summary>
    public string CryptoAlgo
    {
        get => _cryptoAlgo;
        set => SetProperty(ref _cryptoAlgo, value);
    }

    // -------------------------------------------------------------- ping-only

    /// <summary>There is nothing to discover this from without SNMP, so it has to be typed.</summary>
    public string SysName
    {
        get => _sysName;
        set => SetProperty(ref _sysName, value);
    }

    public string Hardware
    {
        get => _hardware;
        set => SetProperty(ref _hardware, value);
    }

    /// <summary>
    /// Free text with autocomplete suggestions from <see cref="KnownOperatingSystems"/>
    /// rather than a closed list - LibreNMS accepts any short name here, and
    /// the fleet's existing OSes are a starting point, not the full set of
    /// ones it understands.
    /// </summary>
    public string Os
    {
        get => _os;
        set => SetProperty(ref _os, value);
    }

    // --------------------------------------------------------------- options

    /// <summary>Blank leaves it to whatever LibreNMS has configured as its default SNMP port.</summary>
    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>udp, tcp, udp6 or tcp6 - blank leaves it to LibreNMS's own default (udp).</summary>
    public string Transport
    {
        get => _transport;
        set => SetProperty(ref _transport, value);
    }

    /// <summary>
    /// A location by name (LibreNMS's add_device <c>location</c>) - an
    /// existing one, or a new name, which LibreNMS creates. Blank leaves it to
    /// whatever the device reports (its sysLocation).
    /// </summary>
    public string Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    /// <summary>
    /// Which poller in a distributed-poller setup should own this device -
    /// <see cref="DefaultPollerGroup"/> leaves it to LibreNMS's own default
    /// (group 0, the main poller). Easy to miss but not optional in practice
    /// for any instance that actually runs more than one poller: a device
    /// added to the wrong group (or left on the default when it should not
    /// be) never gets polled by the poller that's actually watching for it.
    /// </summary>
    public PollerGroup SelectedPollerGroup
    {
        get => _selectedPollerGroup;
        set => SetProperty(ref _selectedPollerGroup, value);
    }

    /// <summary>Skips duplicate/reachability checks - for a device that cannot answer SNMP right now but should still be added.</summary>
    public bool ForceAdd
    {
        get => _forceAdd;
        set => SetProperty(ref _forceAdd, value);
    }

    /// <summary>Falls back to ICMP-only if the SNMP checks fail, instead of failing the add outright.</summary>
    public bool PingFallback
    {
        get => _pingFallback;
        set => SetProperty(ref _pingFallback, value);
    }

    /// <summary>The POST /devices request for a hostname with these settings. Blank optional fields are left out, so LibreNMS applies its own defaults.</summary>
    public AddDeviceRequest BuildRequest(string hostname)
    {
        var pollerGroup = SelectedPollerGroup.Id == 0 ? null : (int?)SelectedPollerGroup.Id;
        var forceAdd = ForceAdd ? true : (bool?)null;
        var location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim();

        if (IsPingOnly)
        {
            return new AddDeviceRequest
            {
                Hostname = hostname.Trim(),
                SnmpDisabled = true,
                Os = string.IsNullOrWhiteSpace(Os) ? null : Os.Trim(),
                SysName = IsBulk || string.IsNullOrWhiteSpace(SysName) ? null : SysName.Trim(),
                Hardware = IsBulk || string.IsNullOrWhiteSpace(Hardware) ? null : Hardware.Trim(),
                PollerGroup = pollerGroup,
                ForceAdd = forceAdd,
                Location = location,
            };
        }

        var port = int.TryParse(Port, out var parsedPort) && parsedPort > 0 ? parsedPort : (int?)null;
        var transport = string.IsNullOrWhiteSpace(Transport) ? null : Transport.Trim();
        var pingFallback = PingFallback ? true : (bool?)null;

        if (IsSnmpV3)
        {
            return new AddDeviceRequest
            {
                Hostname = hostname.Trim(),
                SnmpVersion = "v3",
                AuthLevel = AuthLevel,
                AuthName = string.IsNullOrWhiteSpace(AuthName) ? null : AuthName.Trim(),
                AuthPass = string.IsNullOrWhiteSpace(AuthPass) ? null : AuthPass,
                AuthAlgo = AuthAlgo,
                CryptoPass = string.IsNullOrWhiteSpace(CryptoPass) ? null : CryptoPass,
                CryptoAlgo = CryptoAlgo,
                Port = port,
                Transport = transport,
                PollerGroup = pollerGroup,
                ForceAdd = forceAdd,
                PingFallback = pingFallback,
                Location = location,
            };
        }

        return new AddDeviceRequest
        {
            Hostname = hostname.Trim(),
            SnmpVersion = IsSnmpV1 ? "v1" : "v2c",
            Community = string.IsNullOrWhiteSpace(Community) ? null : Community.Trim(),
            Port = port,
            Transport = transport,
            PollerGroup = pollerGroup,
            ForceAdd = forceAdd,
            PingFallback = pingFallback,
            Location = location,
        };
    }
}
