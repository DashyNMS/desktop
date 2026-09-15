using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the device list window. The device list, and its
/// per-device maintenance-window status, both come from the shared
/// <see cref="DeviceMonitor"/> (also used by Alerts/Health/Dashboard's own
/// on-demand device-name lookups via <see cref="IDeviceCache"/>, and by the
/// Dashboard's "device status" widget), so this tab being open never costs
/// its own extra poll or maintenance scan of the same data.
/// </summary>
public sealed class DeviceListViewModel : ObservableObject, IDisposable
{
    private readonly DeviceMonitor _deviceMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILogger<DeviceListViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, DeviceItemViewModel> _index = new();

    private DeviceItemViewModel? _selectedDevice;
    private string _statusMessage = "Not loaded yet.";
    private string? _errorMessage;
    private bool _isBusy;
    private DateTimeOffset? _lastUpdated;
    private string _searchText = string.Empty;
    private bool _hasLoadedOnce;

    private bool _showUp = true;
    private bool _showDown = true;
    private bool _showDisabled = true;
    private bool _showMaintenance = true;

    private readonly Dictionary<string, DeviceTypeFilterViewModel> _typeFilterIndex = new();
    private string _typeFilterSearchText = string.Empty;
    private bool _isTypeFilterOpen;

    private readonly AutoRefreshTimer _autoRefresh;

    public DeviceListViewModel(
        DeviceMonitor deviceMonitor,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILogger<DeviceListViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _session = session;
        _settings = settings;
        _windows = windows;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Devices = new ObservableCollection<DeviceItemViewModel>();
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = FilterDevice;

        TypeFilters = new ObservableCollection<DeviceTypeFilterViewModel>();
        TypeFiltersView = CollectionViewSource.GetDefaultView(TypeFilters);
        TypeFiltersView.Filter = FilterTypeFilterEntry;

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _deviceMonitor.RequestRefresh();
            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        ShowDeviceDetailCommand = new RelayCommand(ShowSelectedDeviceDetail, () => SelectedDevice is not null);
        ShowAlertsCommand = new RelayCommand(ShowAlertsForSelected, () => SelectedDevice is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);

        CheckAllTypesCommand = new RelayCommand(() => SetAllTypesChecked(true));
        UncheckAllTypesCommand = new RelayCommand(() => SetAllTypesChecked(false));

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        _settings.Changed += OnSettingsChanged;
        _deviceMonitor.PollStarted += OnPollStarted;
        _deviceMonitor.Polled += OnPolled;
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; }

    public ICollectionView DevicesView { get; }

    /// <summary>
    /// One entry per distinct <see cref="Device.Type"/> actually present in
    /// the fleet, alphabetical by <see cref="DeviceTypeFilterViewModel.DisplayText"/> -
    /// a fixed order, unlike sorting by count, so the badges do not shuffle
    /// position as counts fluctuate poll to poll. Built dynamically rather
    /// than a fixed enum like <see cref="DeviceState"/>, since LibreNMS's own
    /// type list is open-ended and varies by what is actually being monitored.
    /// </summary>
    public ObservableCollection<DeviceTypeFilterViewModel> TypeFilters { get; }

    /// <summary>TypeFilters, filtered by <see cref="TypeFilterSearchText"/> - what the filter popover's checklist actually binds to.</summary>
    public ICollectionView TypeFiltersView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ShowDeviceDetailCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public RelayCommand CheckAllTypesCommand { get; }

    public RelayCommand UncheckAllTypesCommand { get; }

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

    /// <summary>Search box inside the type filter popover - narrows <see cref="TypeFiltersView"/>, not the device list itself.</summary>
    public string TypeFilterSearchText
    {
        get => _typeFilterSearchText;
        set
        {
            if (SetProperty(ref _typeFilterSearchText, value))
            {
                TypeFiltersView.Refresh();
            }
        }
    }

    public bool IsTypeFilterOpen
    {
        get => _isTypeFilterOpen;
        set
        {
            if (SetProperty(ref _isTypeFilterOpen, value) && !value)
            {
                // Clears on close, not on open, so it does not visibly empty
                // itself while still visible to the user.
                TypeFilterSearchText = string.Empty;
            }
        }
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
                ShowDeviceDetailCommand.RaiseCanExecuteChanged();
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

        ApplyDevices(result.Devices, result.DeviceIdsUnderMaintenance);

        _hasLoadedOnce = true;
        _lastUpdated = result.CompletedAt;

        StatusMessage = MaintenanceCount > 0
            ? $"{UpCount} up, {DownCount} down, {MaintenanceCount} in maintenance, {TotalCount} total."
            : $"{UpCount} up, {DownCount} down, {TotalCount} total.";
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    private void ApplyDevices(IReadOnlyList<Device> devices, IReadOnlySet<int> maintenanceIds)
    {
        var nameStyle = _settings.Current.DeviceNameStyle;

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
                existing.Update(device, nameStyle);
                existing.IsUnderMaintenance = maintenanceIds.Contains(device.DeviceId);

                var currentIndex = Devices.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Devices.Count)
                {
                    Devices.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new DeviceItemViewModel(device, nameStyle)
                {
                    IsUnderMaintenance = maintenanceIds.Contains(device.DeviceId),
                };
                _index[device.DeviceId] = item;
                Devices.Insert(Math.Min(target, Devices.Count), item);
            }
        }

        ApplyTypeFilters(ordered);
        RaiseCountsChanged();

        if (SelectedDevice is not null && !_index.ContainsKey(SelectedDevice.DeviceId))
        {
            SelectedDevice = null;
        }
    }

    /// <summary>
    /// Adds a <see cref="DeviceTypeFilterViewModel"/> for any type just seen
    /// for the first time (checked by default, so a newly-appearing type does
    /// not silently hide devices), drops any that no longer appear at all,
    /// and refreshes every entry's count - preserving each existing entry's
    /// checked state rather than resetting it every poll.
    /// </summary>
    private void ApplyTypeFilters(IReadOnlyList<Device> ordered)
    {
        var countsByType = ordered
            .GroupBy(d => d.Type ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Count());

        for (var i = TypeFilters.Count - 1; i >= 0; i--)
        {
            var type = TypeFilters[i].Type;
            if (!countsByType.ContainsKey(type))
            {
                _typeFilterIndex.Remove(type);
                TypeFilters.RemoveAt(i);
            }
        }

        foreach (var (type, count) in countsByType.OrderBy(kv => DeviceTypeFilterViewModel.DisplayTextFor(kv.Key), StringComparer.OrdinalIgnoreCase))
        {
            if (_typeFilterIndex.TryGetValue(type, out var existing))
            {
                existing.Count = count;
                continue;
            }

            var filter = new DeviceTypeFilterViewModel(type, OnFilterChanged) { Count = count };
            _typeFilterIndex[type] = filter;

            var insertAt = TypeFilters.TakeWhile(f =>
                string.Compare(f.DisplayText, filter.DisplayText, StringComparison.OrdinalIgnoreCase) < 0).Count();
            TypeFilters.Insert(insertAt, filter);
        }
    }

    // --------------------------------------------------------------- commands

    private void ShowSelectedDeviceDetail()
    {
        if (SelectedDevice is { } device)
        {
            _windows.ShowDeviceDetail(device.DeviceId);
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

        foreach (var filter in TypeFilters)
        {
            filter.SetCheckedQuietly(true);
        }

        OnFilterChanged();
    }

    /// <summary>Shift-click on a status badge: show only that status, hiding the rest.</summary>
    public void IsolateState(DeviceState state)
    {
        _showUp = state == DeviceState.Up;
        _showDown = state == DeviceState.Down;
        _showMaintenance = state == DeviceState.Maintenance;
        _showDisabled = state is DeviceState.Disabled or DeviceState.Ignored;

        OnPropertyChanged(nameof(ShowUp));
        OnPropertyChanged(nameof(ShowDown));
        OnPropertyChanged(nameof(ShowMaintenance));
        OnPropertyChanged(nameof(ShowDisabled));
        OnFilterChanged();
    }

    private void SetAllTypesChecked(bool value)
    {
        foreach (var filter in TypeFilters)
        {
            filter.SetCheckedQuietly(value);
        }

        OnFilterChanged();
    }

    // ---------------------------------------------------------------- helpers

    private bool FilterTypeFilterEntry(object item)
    {
        if (item is not DeviceTypeFilterViewModel filter)
        {
            return false;
        }

        var term = TypeFilterSearchText;
        return string.IsNullOrWhiteSpace(term) || filter.DisplayText.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

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

        var typeKey = device.Model.Type ?? string.Empty;
        if (_typeFilterIndex.TryGetValue(typeKey, out var typeFilter) && !typeFilter.IsChecked)
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
    }
}

/// <summary>
/// One status badge in the Devices tab's type-filter row - one of LibreNMS's
/// own device types (e.g. "network", "server", "wireless"), built dynamically
/// from whatever the fleet actually reports rather than a fixed list.
/// </summary>
public sealed class DeviceTypeFilterViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isChecked = true;
    private int _count;

    public DeviceTypeFilterViewModel(string type, Action onChanged)
    {
        Type = type;
        _onChanged = onChanged;
    }

    /// <summary>The raw LibreNMS value, e.g. "network" - empty for a device with no type set.</summary>
    public string Type { get; }

    public string DisplayText => DisplayTextFor(Type);

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(LabelText));
            }
        }
    }

    /// <summary>"Network (209)" - a real string property rather than a Content/StringFormat combination, which silently does nothing on an object-typed property like CheckBox.Content (confirmed the hard way elsewhere in this app - see Expander.Header's remarks in DeviceView.xaml).</summary>
    public string LabelText => $"{DisplayText} ({Count})";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onChanged();
            }
        }
    }

    /// <summary>Sets <see cref="IsChecked"/> without invoking the change callback - for a caller (ClearFilters, SetAllTypesChecked) updating several of these at once, which raises the one filter refresh itself afterwards.</summary>
    public void SetCheckedQuietly(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));

    /// <summary>"Unspecified" for a device with no type set - LibreNMS's own web UI leaves this blank rather than naming it, but a blank filter badge would be confusing.</summary>
    public static string DisplayTextFor(string type) =>
        string.IsNullOrWhiteSpace(type) ? "Unspecified" : char.ToUpperInvariant(type[0]) + type[1..];
}
