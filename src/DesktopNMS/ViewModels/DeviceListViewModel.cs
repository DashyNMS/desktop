using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the device list window. The device list itself comes
/// from the shared <see cref="DeviceMonitor"/> (also used by Alerts/Health/
/// Dashboard's own on-demand device-name lookups via <see cref="IDeviceCache"/>),
/// so this tab being open never costs its own extra poll of the same data.
/// Per-device maintenance-window checks (see <see cref="RefreshMaintenanceStatusAsync"/>)
/// are a separate concern with no bulk endpoint, so they stay local to this view model.
/// </summary>
public sealed class DeviceListViewModel : ObservableObject, IDisposable
{
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILibreNmsClient _client;
    private readonly ILogger<DeviceListViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, DeviceItemViewModel> _index = new();

    /// <summary>
    /// LibreNMS's versioned API has no bulk "which devices are in
    /// maintenance" call, only GET /devices/{id}/maintenance - one device at a
    /// time (see <see cref="IDevicesApi.IsUnderMaintenanceAsync"/>). Checking a
    /// fleet of hundreds of devices therefore means hundreds of requests. This
    /// caps how many run at once so a refresh does not look like a burst
    /// against the LibreNMS server.
    /// </summary>
    private const int MaxConcurrentMaintenanceChecks = 16;

    /// <summary>
    /// Maintenance windows do not change second to second, so a trigger-happy
    /// Refresh click within this window reuses the last scan instead of paying
    /// for hundreds of requests again.
    /// </summary>
    private static readonly TimeSpan MinimumMaintenanceRescanInterval = TimeSpan.FromSeconds(60);

    private CancellationTokenSource? _maintenanceCts;
    private DeviceItemViewModel? _selectedDevice;
    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private DateTimeOffset? _lastUpdated;
    private DateTimeOffset? _maintenanceLastScanned;
    private string _searchText = string.Empty;
    private bool _hasLoadedOnce;

    private bool _showUp = true;
    private bool _showDown = true;
    private bool _showDisabled = true;
    private bool _showMaintenance = true;

    private readonly AutoRefreshTimer _autoRefresh;

    public DeviceListViewModel(
        DeviceMonitor deviceMonitor,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILibreNmsClient client,
        ILogger<DeviceListViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _session = session;
        _settings = settings;
        _windows = windows;
        _client = client;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Devices = new ObservableCollection<DeviceItemViewModel>();
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = FilterDevice;

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _deviceMonitor.RequestRefresh();
            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        OpenDeviceCommand = new RelayCommand(OpenSelectedDevice, () => SelectedDevice?.DeviceUrl is not null);
        ShowAlertsCommand = new RelayCommand(ShowAlertsForSelected, () => SelectedDevice is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        _settings.Changed += OnSettingsChanged;
        _deviceMonitor.PollStarted += OnPollStarted;
        _deviceMonitor.Polled += OnPolled;
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; }

    public ICollectionView DevicesView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>A short "45s" / "2:05" countdown to the next automatic refresh.</summary>
    public string NextRefreshText => PollAlignment.FormatRemaining(_deviceMonitor.SecondsUntilNextPoll());

    // -------------------------------------------------------------- filtering

    public bool ShowUp
    {
        get => _showUp;
        set { if (SetProperty(ref _showUp, value)) OnFilterChanged(); }
    }

    public bool ShowDown
    {
        get => _showDown;
        set { if (SetProperty(ref _showDown, value)) OnFilterChanged(); }
    }

    /// <summary>Covers both Disabled and Ignored: neither is actively monitored.</summary>
    public bool ShowDisabled
    {
        get => _showDisabled;
        set { if (SetProperty(ref _showDisabled, value)) OnFilterChanged(); }
    }

    public bool ShowMaintenance
    {
        get => _showMaintenance;
        set { if (SetProperty(ref _showMaintenance, value)) OnFilterChanged(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) OnFilterChanged(); }
    }

    // ------------------------------------------------------------------ state

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OpenDeviceCommand.RaiseCanExecuteChanged();
                ShowAlertsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => SelectedDevice is not null;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LastUpdatedText => _lastUpdated is null
        ? "never"
        : _lastUpdated.Value.LocalDateTime.ToString("HH:mm:ss");

    public int UpCount => Devices.Count(d => d.State == DeviceState.Up);

    public int DownCount => Devices.Count(d => d.State == DeviceState.Down);

    public int MaintenanceCount => Devices.Count(d => d.State == DeviceState.Maintenance);

    /// <summary>Covers both Disabled and Ignored, same as the <see cref="ShowDisabled"/> filter.</summary>
    public int DisabledCount => Devices.Count(d => d.State is DeviceState.Disabled or DeviceState.Ignored);

    public int TotalCount => Devices.Count;

    public int VisibleCount => DevicesView.Cast<object>().Count();

    // --------------------------------------------------------------- lifetime

    /// <summary>
    /// Called each time the tab is shown; starts the shared device monitor if
    /// nothing else has already, and asks it to poll right away so this tab is
    /// not left empty until the next scheduled tick.
    /// </summary>
    public void OnShown()
    {
        _deviceMonitor.Start();

        // Always started, never gated on _hasLoadedOnce - see the matching
        // comment in HealthViewModel: a poll can mark this one loaded before
        // it is ever shown, which would otherwise skip starting the countdown
        // and leave it frozen between polls.
        _autoRefresh.Start();

        if (_hasLoadedOnce)
        {
            return;
        }

        _deviceMonitor.RequestRefresh();
    }

    private void OnPollStarted(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() => IsBusy = true);

    private void OnPolled(object? sender, DevicePollResult result) => _dispatcher.InvokeAsync(() => ApplyPollResult(result));

    private void ApplyPollResult(DevicePollResult result)
    {
        IsBusy = false;
        OnPropertyChanged(nameof(NextRefreshText));

        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            StatusMessage = "Last refresh failed.";
            return;
        }

        ErrorMessage = null;

        ApplyDevices(result.Devices);

        _hasLoadedOnce = true;
        _lastUpdated = result.CompletedAt;

        _ = RefreshMaintenanceStatusAsync();

        StatusMessage = MaintenanceCount > 0
            ? $"{UpCount} up, {DownCount} down, {MaintenanceCount} in maintenance, {TotalCount} total."
            : $"{UpCount} up, {DownCount} down, {TotalCount} total.";
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void ApplyDevices(IReadOnlyList<Device> devices)
    {
        var nameStyle = _settings.Current.DeviceNameStyle;
        var connection = _session.Connection;

        var ordered = devices
            .OrderBy(d => nameStyle.Resolve(d, d.Hostname), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incoming = ordered.Select(d => d.DeviceId).ToHashSet();

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Devices[i].DeviceId))
            {
                _index.Remove(Devices[i].DeviceId);
                Devices.RemoveAt(i);
            }
        }

        for (var target = 0; target < ordered.Count; target++)
        {
            var device = ordered[target];

            if (_index.TryGetValue(device.DeviceId, out var existing))
            {
                existing.Update(device, nameStyle, connection);

                var currentIndex = Devices.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Devices.Count)
                {
                    Devices.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new DeviceItemViewModel(device, nameStyle, connection);
                _index[device.DeviceId] = item;
                Devices.Insert(Math.Min(target, Devices.Count), item);
            }
        }

        RaiseCountsChanged();

        if (SelectedDevice is not null && !_index.ContainsKey(SelectedDevice.DeviceId))
        {
            SelectedDevice = null;
        }
    }

    /// <summary>
    /// Checks every currently-loaded device for an active maintenance window,
    /// bounded to <see cref="MaxConcurrentMaintenanceChecks"/> requests at
    /// once. Skips the scan if the last one finished too recently to be worth
    /// repeating. A single device's check failing is logged and treated as
    /// "not in maintenance" rather than losing the whole scan.
    /// </summary>
    private async Task RefreshMaintenanceStatusAsync()
    {
        if (_maintenanceLastScanned is { } last && DateTimeOffset.UtcNow - last < MinimumMaintenanceRescanInterval)
        {
            return;
        }

        var items = Devices.ToList();
        if (items.Count == 0)
        {
            return;
        }

        _maintenanceCts?.Cancel();
        _maintenanceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _maintenanceCts = cts;
        var cancellationToken = cts.Token;

        using var gate = new SemaphoreSlim(MaxConcurrentMaintenanceChecks);
        var failures = 0;

        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var isUnderMaintenance = await _client.Devices
                    .IsUnderMaintenanceAsync(item.DeviceId, cancellationToken)
                    .ConfigureAwait(true);

                item.IsUnderMaintenance = isUnderMaintenance;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Superseded by a newer refresh; leave the flag as it was.
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                _logger.LogDebug(ex, "Could not check maintenance status for device {DeviceId}", item.DeviceId);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _maintenanceLastScanned = DateTimeOffset.UtcNow;
        RaiseCountsChanged();

        if (failures > 0)
        {
            _logger.LogWarning(
                "Maintenance status could not be checked for {Failures} of {Total} device(s)",
                failures,
                items.Count);
        }
    }

    // --------------------------------------------------------------- commands

    private void OpenSelectedDevice()
    {
        if (SelectedDevice?.DeviceUrl is { } url)
        {
            _windows.OpenUrl(url);
        }
    }

    private void ShowAlertsForSelected()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        // Hostname is what the alerts list actually has to search against; the
        // device list may be showing sysName or the display name instead.
        // Routed through IWindowService (rather than depending on MainViewModel
        // directly) so the two view models do not depend on each other.
        _windows.ShowAlertsForDevice(device.Model.Hostname ?? device.Name);
    }

    private void ClearFilters()
    {
        ShowUp = true;
        ShowDown = true;
        ShowDisabled = true;
        ShowMaintenance = true;
        SearchText = string.Empty;
    }

    // ---------------------------------------------------------------- helpers

    private bool FilterDevice(object item)
    {
        if (item is not DeviceItemViewModel device)
        {
            return false;
        }

        var stateAllowed = device.State switch
        {
            DeviceState.Up => ShowUp,
            DeviceState.Down => ShowDown,
            DeviceState.Maintenance => ShowMaintenance,
            _ => ShowDisabled,
        };

        if (!stateAllowed)
        {
            return false;
        }

        var term = SearchText;
        return string.IsNullOrWhiteSpace(term) || device.Matches(term.Trim());
    }

    private void OnFilterChanged()
    {
        DevicesView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var nameStyle = settings.DeviceNameStyle;

        foreach (var device in Devices)
        {
            device.ApplyNameStyle(nameStyle);
        }
    }

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(UpCount));
        OnPropertyChanged(nameof(DownCount));
        OnPropertyChanged(nameof(MaintenanceCount));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));

        // A maintenance scan can move rows in or out of the current filter
        // without the ObservableCollection itself changing, so the view needs
        // an explicit nudge to re-evaluate FilterDevice.
        DevicesView.Refresh();
    }

    public void Dispose()
    {
        _autoRefresh.Dispose();
        _settings.Changed -= OnSettingsChanged;
        _deviceMonitor.PollStarted -= OnPollStarted;
        _deviceMonitor.Polled -= OnPolled;
        _maintenanceCts?.Cancel();
        _maintenanceCts?.Dispose();
    }
}
