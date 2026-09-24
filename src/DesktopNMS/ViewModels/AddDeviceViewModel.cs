using System;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add device" dialog (see <see cref="Views.AddDeviceWindow"/> and
/// <see cref="IDevicesApi.AddAsync"/>): a hostname plus the settings in
/// <see cref="Options"/>, which "Bulk add devices" shares. Stays open with an
/// inline error on failure (e.g. unreachable SNMP) rather than closing, the
/// same shape as <see cref="ConnectionViewModel"/>'s sign-in dialog, so a
/// wrong community string or similar can be fixed and retried without
/// re-entering everything.
/// </summary>
public sealed class AddDeviceViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly IWindowService _windows;

    private string _hostname = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;

    public AddDeviceViewModel(ILibreNmsClient client, IWindowService windows, ILogger<AddDeviceViewModel> logger)
    {
        _client = client;
        _windows = windows;

        Options = new DeviceAddOptionsViewModel(client, logger, isBulk: false);
        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Hostname));
        BulkAddCommand = new RelayCommand(() => RequestBulkAdd?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Raised with true once a device has been added. Cancelling is handled by the dialog itself.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>"Adding several?" - closes this dialog so the bulk one can open in its place.</summary>
    public event EventHandler? RequestBulkAdd;

    public DeviceAddOptionsViewModel Options { get; }

    public AsyncRelayCommand AddCommand { get; }

    public RelayCommand BulkAddCommand { get; }

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

        var request = Options.BuildRequest(Hostname);
        if (BulkDeviceImport.RequestError(request) is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _client.Devices.AddAsync(request).ConfigureAwait(true);
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
}
