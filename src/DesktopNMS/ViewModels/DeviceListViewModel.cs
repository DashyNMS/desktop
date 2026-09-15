using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
/// View model behind the device list window. The device list, and its
/// per-device maintenance-window status, both come from the shared
/// <see cref="DeviceMonitor"/> (also used by Alerts/Health/Dashboard's own
/// on-demand device-name lookups via <see cref="IDeviceCache"/>, and by the
/// Dashboard's "device status" widget), so this tab being open never costs
/// its own extra poll or maintenance scan of the same data.
/// </summary>
public sealed class DeviceListViewModel : ObservableObject, IDisposable
{
    /// <summary>The <see cref="GroupFilter"/> key used for a device that belongs to no group at all.</summary>
    private static readonly IReadOnlyList<string> NoGroupKey = new[] { string.Empty };

    private readonly DeviceMonitor _deviceMonitor;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILibreNmsClient _client;
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
    private bool _hasLoadedGroupsOnce;

    private bool _showUp = true;
    private bool _showDown = true;
    private bool _showDisabled = true;
    private bool _showMaintenance = true;

    /// <summary>Device id -> the names of every group it belongs to. Empty until <see cref="LoadDeviceGroupsAsync"/> first completes.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _groupMembership = new Dictionary<int, IReadOnlyList<string>>();

    /// <summary>The device list from the most recent poll, kept so group membership arriving separately (see <see cref="LoadDeviceGroupsAsync"/>) can rebuild <see cref="GroupFilter"/> without waiting for the next poll.</summary>
    private IReadOnlyList<Device> _lastOrderedDevices = Array.Empty<Device>();

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

        TypeFilter = new FilterFacet(OnFilterChanged);
        LocationFilter = new FilterFacet(OnFilterChanged);
        GroupFilter = new FilterFacet(OnFilterChanged);

        TypeFilter.PropertyChanged += OnFacetPropertyChanged;
        LocationFilter.PropertyChanged += OnFacetPropertyChanged;
        GroupFilter.PropertyChanged += OnFacetPropertyChanged;

        RefreshCommand = new AsyncRelayCommand(() =>
        {
            _deviceMonitor.RequestRefresh();

            // Fire-and-forget rather than awaited: group membership is a
            // separate, slower fetch (one call per group) from the device
            // poll this command otherwise only requests, and awaiting it here
            // would leave the Refresh button disabled for that whole time.
            // LoadDeviceGroupsAsync handles its own failures.
            _ = LoadDeviceGroupsAsync();

            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        ShowDeviceDetailCommand = new RelayCommand(ShowSelectedDeviceDetail, () => SelectedDevice is not null);
        ShowAlertsCommand = new RelayCommand(ShowAlertsForSelected, () => SelectedDevice is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ShowFiltersCommand = new RelayCommand(ShowFiltersDialog);

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        _settings.Changed += OnSettingsChanged;
        _deviceMonitor.PollStarted += OnPollStarted;
        _deviceMonitor.Polled += OnPolled;
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; }

    public ICollectionView DevicesView { get; }

    /// <summary>
    /// One entry per distinct <see cref="Device.Type"/> actually present in
    /// the fleet. Built dynamically rather than a fixed enum like
    /// <see cref="DeviceState"/>, since LibreNMS's own type list is
    /// open-ended and varies by what is actually being monitored.
    /// </summary>
    public FilterFacet TypeFilter { get; }

    /// <summary>One entry per distinct <see cref="Device.Location"/> actually present in the fleet.</summary>
    public FilterFacet LocationFilter { get; }

    /// <summary>
    /// One entry per LibreNMS device group that has at least one member in
    /// the fleet (see <see cref="LoadDeviceGroupsAsync"/>) - unlike Type and
    /// Location, a device can belong to more than one group at once, so this
    /// facet's counting and matching both work in terms of a set of keys per
    /// device rather than a single one.
    /// </summary>
    public FilterFacet GroupFilter { get; }

    /// <summary>True as soon as any of the three facets has something unchecked - drives the dot on the Devices tab's Filters button.</summary>
    public bool IsFiltersActive => TypeFilter.HasActiveFilter || LocationFilter.HasActiveFilter || GroupFilter.HasActiveFilter;

    /// <summary>
    /// True when the device list is filtered in any way at all - a facet, a
    /// hidden status, or a search term - broader than <see cref="IsFiltersActive"/>,
    /// which only covers the Filters dialog's three facets. Drives whether
    /// the Clear button does anything, so it isn't left clickable with
    /// nothing to clear.
    /// </summary>
    public bool HasAnyFilterApplied =>
        !string.IsNullOrEmpty(SearchText)
        || !ShowUp || !ShowDown || !ShowMaintenance || !ShowDisabled
        || IsFiltersActive;

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand ShowDeviceDetailCommand { get; }

    public RelayCommand ShowAlertsCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    /// <summary>Opens the centered Type/Location/Group filter dialog (see <see cref="IWindowService.ShowDeviceFiltersDialog"/>).</summary>
    public RelayCommand ShowFiltersCommand { get; }

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

        if (!_hasLoadedGroupsOnce)
        {
            _hasLoadedGroupsOnce = true;
            _ = LoadDeviceGroupsAsync();
        }

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

        _lastOrderedDevices = ordered;

        TypeFilter.Apply(ordered
            .GroupBy(d => d.Type ?? string.Empty)
            .Select(g => (g.Key, TypeDisplayText(g.Key), g.Count())));

        LocationFilter.Apply(ordered
            .GroupBy(d => d.Location ?? string.Empty)
            .Select(g => (g.Key, LocationDisplayText(g.Key), g.Count())));

        RebuildGroupFilter(ordered);

        RaiseCountsChanged();

        if (SelectedDevice is not null && !_index.ContainsKey(SelectedDevice.DeviceId))
        {
            SelectedDevice = null;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="GroupFilter"/>'s counts from the current device
    /// list against whatever group membership is currently known - called
    /// both after a device poll and after <see cref="LoadDeviceGroupsAsync"/>
    /// completes, since either can change independently of the other.
    /// </summary>
    private void RebuildGroupFilter(IReadOnlyList<Device> ordered)
    {
        var counts = new Dictionary<string, int>();

        foreach (var device in ordered)
        {
            var groups = _groupMembership.TryGetValue(device.DeviceId, out var names) && names.Count > 0
                ? names
                : NoGroupKey;

            foreach (var group in groups)
            {
                counts[group] = counts.TryGetValue(group, out var existing) ? existing + 1 : 1;
            }
        }

        GroupFilter.Apply(counts.Select(kv => (kv.Key, GroupDisplayText(kv.Key), kv.Value)));
    }

    /// <summary>
    /// Fetches every device group's membership (see <see cref="IDeviceGroupsApi.GetMembershipByDeviceAsync"/>)
    /// and rebuilds <see cref="GroupFilter"/> from it. Deliberately separate
    /// from the regular device poll: LibreNMS has no bulk endpoint for this,
    /// so building it costs one call per group, worth doing on tab load and
    /// on an explicit refresh rather than on every 30-second background poll.
    /// </summary>
    private async Task LoadDeviceGroupsAsync()
    {
        if (!_session.IsConnected)
        {
            return;
        }

        try
        {
            var membership = await _client.DeviceGroups.GetMembershipByDeviceAsync().ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                _groupMembership = membership;
                RebuildGroupFilter(_lastOrderedDevices);
            });
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load device group membership");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load device group membership unexpectedly");
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

    private void ShowFiltersDialog()
    {
        _windows.ShowDeviceFiltersDialog();

        // Cleared after the dialog closes (ShowDeviceFiltersDialog blocks
        // until then), not while it's open, so none of the three search
        // boxes visibly empties itself while still visible to the user.
        TypeFilter.ClearSearch();
        LocationFilter.ClearSearch();
        GroupFilter.ClearSearch();
    }

    private void ClearFilters()
    {
        ShowUp = true;
        ShowDown = true;
        ShowDisabled = true;
        ShowMaintenance = true;
        SearchText = string.Empty;

        TypeFilter.SetAllChecked(true, notify: false);
        LocationFilter.SetAllChecked(true, notify: false);
        GroupFilter.SetAllChecked(true, notify: false);

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

        if (!TypeFilter.Allows(device.Model.Type ?? string.Empty))
        {
            return false;
        }

        if (!LocationFilter.Allows(device.Model.Location ?? string.Empty))
        {
            return false;
        }

        var groups = _groupMembership.TryGetValue(device.DeviceId, out var names) && names.Count > 0
            ? names
            : NoGroupKey;

        if (!GroupFilter.AllowsAny(groups))
        {
            return false;
        }

        var term = SearchText;
        return string.IsNullOrWhiteSpace(term) || device.Matches(term.Trim());
    }

    /// <summary>"Unspecified" for a device with no type set - LibreNMS's own web UI leaves this blank rather than naming it, but a blank filter entry would be confusing.</summary>
    private static string TypeDisplayText(string type) =>
        string.IsNullOrWhiteSpace(type) ? "Unspecified" : char.ToUpperInvariant(type[0]) + type[1..];

    private static string LocationDisplayText(string location) =>
        string.IsNullOrWhiteSpace(location) ? "Unspecified" : location;

    private static string GroupDisplayText(string group) =>
        string.IsNullOrWhiteSpace(group) ? "Not in a group" : group;

    private void OnFilterChanged()
    {
        DevicesView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasAnyFilterApplied));
    }

    /// <summary>
    /// Relays a facet's own <see cref="FilterFacet.HasActiveFilter"/> change
    /// into this view model's aggregate properties - Apply() can flip it
    /// without going through OnFilterChanged (e.g. the last unchecked group
    /// disappearing from the fleet), so this listens directly rather than
    /// relying only on the checkbox-toggle path.
    /// </summary>
    private void OnFacetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilterFacet.HasActiveFilter))
        {
            return;
        }

        OnPropertyChanged(nameof(IsFiltersActive));
        OnPropertyChanged(nameof(HasAnyFilterApplied));
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
        TypeFilter.PropertyChanged -= OnFacetPropertyChanged;
        LocationFilter.PropertyChanged -= OnFacetPropertyChanged;
        GroupFilter.PropertyChanged -= OnFacetPropertyChanged;
    }
}
