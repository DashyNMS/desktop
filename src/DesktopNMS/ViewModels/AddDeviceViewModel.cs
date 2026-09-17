using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add device" dialog (see <see cref="Views.AddDeviceWindow"/> and
/// <see cref="IDevicesApi.AddAsync"/>). Stays open with an inline error on
/// failure (e.g. unreachable SNMP) rather than closing, the same shape as
/// <see cref="ConnectionViewModel"/>'s sign-in dialog, so a wrong community
/// string or similar can be fixed and retried without re-entering everything.
/// </summary>
public sealed class AddDeviceViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly IWindowService _windows;
    private readonly ILogger<AddDeviceViewModel> _logger;

    /// <summary>Always present regardless of what (if anything) the server returns - represents "don't set poller_group at all", which LibreNMS itself defaults to group 0.</summary>
    private static readonly PollerGroup DefaultPollerGroup = new() { Id = 0, GroupName = "Default (poller 0)" };

    private string _hostname = string.Empty;
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
    private PollerGroup _selectedPollerGroup = DefaultPollerGroup;
    private bool _forceAdd;
    private bool _pingFallback = true;
    private bool _isBusy;
    private string? _errorMessage;

    public AddDeviceViewModel(ILibreNmsClient client, IWindowService windows, ILogger<AddDeviceViewModel> logger)
    {
        _client = client;
        _windows = windows;
        _logger = logger;

        PollerGroups = new ObservableCollection<PollerGroup> { DefaultPollerGroup };
        KnownOperatingSystems = new ObservableCollection<string> { "ping" };

        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Hostname));

        _ = LoadPollerGroupsAsync();
        _ = LoadKnownOperatingSystemsAsync();
    }

    /// <summary>
    /// Always has at least <see cref="DefaultPollerGroup"/>; whatever else
    /// LibreNMS reports gets appended once <see cref="LoadPollerGroupsAsync"/>
    /// completes. A single-poller instance with none configured just leaves
    /// this at one entry, which is the correct thing to show either way.
    /// </summary>
    public ObservableCollection<PollerGroup> PollerGroups { get; }

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

    /// <summary>
    /// Suggestions for the ping-only "OS" field (see <see cref="Os"/>), drawn
    /// from whatever OS short names are already in use on this instance's
    /// fleet - there is no dedicated "list valid OS names" endpoint in the
    /// versioned LibreNMS API, but the device list already has to know them.
    /// Always seeded with "ping", LibreNMS's own default when the field is
    /// left unset, regardless of whether any device already uses it.
    /// </summary>
    public ObservableCollection<string> KnownOperatingSystems { get; }

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

    /// <summary>Raised with true once a device has been added. Cancelling is handled by the dialog itself.</summary>
    public event EventHandler<bool>? RequestClose;

    public AsyncRelayCommand AddCommand { get; }

    public string Hostname
    {
        get => _hostname;
        set
        {
            if (SetProperty(ref _hostname, value))
            {
                AddCommand.RaiseCanExecuteChanged();
            }
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

    // ------------------------------------------------------------------ state

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    private async Task AddAsync()
    {
        ErrorMessage = null;
        IsBusy = true;

        try
        {
            var result = await _client.Devices.AddAsync(BuildRequest()).ConfigureAwait(true);
            _windows.ShowInformation("Device added", result.Message);
            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AddDeviceRequest BuildRequest()
    {
        var pollerGroup = SelectedPollerGroup.Id == 0 ? null : (int?)SelectedPollerGroup.Id;
        var forceAdd = ForceAdd ? true : (bool?)null;

        if (IsPingOnly)
        {
            return new AddDeviceRequest
            {
                Hostname = Hostname.Trim(),
                SnmpDisabled = true,
                Os = string.IsNullOrWhiteSpace(Os) ? null : Os.Trim(),
                SysName = string.IsNullOrWhiteSpace(SysName) ? null : SysName.Trim(),
                Hardware = string.IsNullOrWhiteSpace(Hardware) ? null : Hardware.Trim(),
                PollerGroup = pollerGroup,
                ForceAdd = forceAdd,
            };
        }

        var port = int.TryParse(Port, out var parsedPort) && parsedPort > 0 ? parsedPort : (int?)null;
        var transport = string.IsNullOrWhiteSpace(Transport) ? null : Transport.Trim();
        var pingFallback = PingFallback ? true : (bool?)null;

        if (IsSnmpV3)
        {
            return new AddDeviceRequest
            {
                Hostname = Hostname.Trim(),
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
            };
        }

        return new AddDeviceRequest
        {
            Hostname = Hostname.Trim(),
            SnmpVersion = IsSnmpV1 ? "v1" : "v2c",
            Community = string.IsNullOrWhiteSpace(Community) ? null : Community.Trim(),
            Port = port,
            Transport = transport,
            PollerGroup = pollerGroup,
            ForceAdd = forceAdd,
            PingFallback = pingFallback,
        };
    }
}
