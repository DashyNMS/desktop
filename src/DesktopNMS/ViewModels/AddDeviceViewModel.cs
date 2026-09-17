using System;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

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

    private string _hostname = string.Empty;
    private bool _isSnmpV2c = true;
    private bool _isSnmpV1;
    private bool _isSnmpV3;
    private bool _isSnmpDisabled;
    private string _community = "public";
    private string _authLevel = "authPriv";
    private string _authName = string.Empty;
    private string _authPass = string.Empty;
    private string _authAlgo = "SHA";
    private string _cryptoPass = string.Empty;
    private string _cryptoAlgo = "AES";
    private string _port = string.Empty;
    private string _transport = string.Empty;
    private bool _forceAdd;
    private bool _pingFallback = true;
    private bool _isBusy;
    private string? _errorMessage;

    public AddDeviceViewModel(ILibreNmsClient client, IWindowService windows)
    {
        _client = client;
        _windows = windows;

        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Hostname));
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

    // ---------------------------------------------------------- SNMP version

    public bool IsSnmpV2c
    {
        get => _isSnmpV2c;
        set { if (value) SetSnmpMode(v2c: true); }
    }

    public bool IsSnmpV1
    {
        get => _isSnmpV1;
        set { if (value) SetSnmpMode(v1: true); }
    }

    public bool IsSnmpV3
    {
        get => _isSnmpV3;
        set { if (value) SetSnmpMode(v3: true); }
    }

    /// <summary>ICMP-only: no SNMP credentials at all.</summary>
    public bool IsSnmpDisabled
    {
        get => _isSnmpDisabled;
        set { if (value) SetSnmpMode(disabled: true); }
    }

    /// <summary>Shows the community field - v1 and v2c both use it.</summary>
    public bool ShowCommunity => IsSnmpV1 || IsSnmpV2c;

    public bool ShowV3Fields => IsSnmpV3;

    private void SetSnmpMode(bool v2c = false, bool v1 = false, bool v3 = false, bool disabled = false)
    {
        _isSnmpV2c = v2c;
        _isSnmpV1 = v1;
        _isSnmpV3 = v3;
        _isSnmpDisabled = disabled;

        OnPropertyChanged(nameof(IsSnmpV2c));
        OnPropertyChanged(nameof(IsSnmpV1));
        OnPropertyChanged(nameof(IsSnmpV3));
        OnPropertyChanged(nameof(IsSnmpDisabled));
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
        var port = int.TryParse(Port, out var parsedPort) && parsedPort > 0 ? parsedPort : (int?)null;
        var transport = string.IsNullOrWhiteSpace(Transport) ? null : Transport.Trim();
        var forceAdd = ForceAdd ? true : (bool?)null;
        var pingFallback = PingFallback ? true : (bool?)null;

        if (IsSnmpDisabled)
        {
            return new AddDeviceRequest
            {
                Hostname = Hostname.Trim(),
                SnmpDisabled = true,
                Port = port,
                Transport = transport,
                ForceAdd = forceAdd,
                PingFallback = pingFallback,
            };
        }

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
            ForceAdd = forceAdd,
            PingFallback = pingFallback,
        };
    }
}
