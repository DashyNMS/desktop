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
    private readonly IDeviceGroupMembershipService _groupMembership;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly IWindowService _windows;
    private readonly ILibreNmsClient _client;
    private readonly ILogger<DeviceListViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<int, DeviceItemViewModel> _index = new();

    private readonly List<DeviceItemViewModel> _selectedDevices = new();

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

    /// <summary>The device list from the most recent poll, kept so group membership arriving separately (see <see cref="OnGroupMembershipChanged"/>) can rebuild <see cref="GroupFilter"/> without waiting for the next poll.</summary>
    private IReadOnlyList<Device> _lastOrderedDevices = Array.Empty<Device>();

    /// <summary>Device ids currently pinned, kept in step with <see cref="AppSettings.PinnedDevices"/> for cheap per-row lookups in <see cref="ApplyDevices"/>.</summary>
    private HashSet<int> _pinnedIds = new();

    private readonly AutoRefreshTimer _autoRefresh;

    public DeviceListViewModel(
        DeviceMonitor deviceMonitor,
        IDeviceGroupMembershipService groupMembership,
        ISessionService session,
        ISettingsStore settings,
        IWindowService windows,
        ILibreNmsClient client,
        ILogger<DeviceListViewModel> logger)
    {
        _deviceMonitor = deviceMonitor;
        _groupMembership = groupMembership;
        _session = session;
        _settings = settings;
        _windows = windows;
        _client = client;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Devices = new ObservableCollection<DeviceItemViewModel>();
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = FilterDevice;

        // Pinned devices always float to the top, regardless of whichever
        // column the user has sorted by - see DevicesView.xaml.cs's Sorting
        // handler, which re-applies this same IsPinned SortDescription ahead
        // of the clicked column instead of letting the DataGrid replace it.
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceItemViewModel.IsPinned), ListSortDirection.Descending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceItemViewModel.Name), ListSortDirection.Ascending));

        _pinnedIds = _settings.Current.PinnedDevices.Select(p => p.DeviceId).ToHashSet();

        RecentlyViewedDevices = new ObservableCollection<RecentlyViewedDeviceItemViewModel>();
        RebuildRecentlyViewed(_settings.Current.RecentlyViewedDevices);

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
            // The membership service handles its own failures.
            _ = _groupMembership.RefreshAsync();

            return Task.CompletedTask;
        }, () => _session.IsConnected && !IsBusy);

        ShowDeviceDetailCommand = new RelayCommand(ShowSelectedDeviceDetail, () => SelectedDevice is not null);
        ShowAlertsCommand = new RelayCommand(ShowAlertsForSelected, () => SelectedDevice is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ShowFiltersCommand = new RelayCommand(ShowFiltersDialog);
        // No CanExecute gate on _session.IsConnected: RelayCommand only
        // re-evaluates when RaiseCanExecuteChanged() is explicitly called
        // (unlike WPF's own RoutedCommand, there is no automatic requery), and
        // nothing here would ever call it once the session connects after
        // this view model is constructed - unlike RefreshCommand below, which
        // only happens to stay in sync because IsBusy's own setter already
        // raises it for an unrelated reason. Same as Filters/Clear beside it,
        // AddDevice has nothing to gate: a failed add while disconnected
        // surfaces as an inline error in the dialog itself.
        AddDeviceCommand = new RelayCommand(AddDevice);

        // Bulk actions (issue #39) - mirror MainViewModel's Acknowledge/
        // Unacknowledge pair for the Alerts grid: both always available for
        // a multi-selection regardless of each item's own current pin
        // state, rather than one mixed-state toggle.
        PinSelectedCommand = new RelayCommand(() => SetSelectedPinned(true), () => _selectedDevices.Count > 0);
        UnpinSelectedCommand = new RelayCommand(() => SetSelectedPinned(false), () => _selectedDevices.Count > 0);
        AddSelectedToGroupCommand = new RelayCommand(AddSelectedToGroup, () => _selectedDevices.Count > 0);
        RediscoverSelectedCommand = new AsyncRelayCommand(RediscoverSelectedAsync, () => _selectedDevices.Count > 0);

        _autoRefresh = new AutoRefreshTimer(() => OnPropertyChanged(nameof(NextRefreshText)));

        _groupMembership.Changed += OnGroupMembershipChanged;
        _settings.Changed += OnSettingsChanged;
        _deviceMonitor.PollStarted += OnPollStarted;
        _deviceMonitor.Polled += OnPolled;
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; }

    public ICollectionView DevicesView { get; }

    /// <summary>
    /// Devices opened recently (see DeviceDetailViewModel.RecordRecentlyViewed),
    /// most-recent first - a quick way back into something you were just
    /// looking at, shown as a row of chips above the grid.
    /// </summary>
    public ObservableCollection<RecentlyViewedDeviceItemViewModel> RecentlyViewedDevices { get; }

    public bool HasRecentlyViewedDevices => _settings.Current.ShowRecentlyViewedDevices && RecentlyViewedDevices.Count > 0;

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

    /// <summary>Opens the "Add device" dialog - see <see cref="AddDevice"/>.</summary>
    public RelayCommand AddDeviceCommand { get; }

    /// <summary>Pins every currently-selected device - see <see cref="SetSelectedPinned"/>.</summary>
    public RelayCommand PinSelectedCommand { get; }

    /// <summary>Unpins every currently-selected device - see <see cref="SetSelectedPinned"/>.</summary>
    public RelayCommand UnpinSelectedCommand { get; }

    /// <summary>Opens the "Add to group" dialog for every currently-selected device - see <see cref="AddSelectedToGroup"/>.</summary>
    public RelayCommand AddSelectedToGroupCommand { get; }

    /// <summary>Triggers a LibreNMS rediscovery for every currently-selected device - see <see cref="RediscoverSelectedAsync"/>.</summary>
    public AsyncRelayCommand RediscoverSelectedCommand { get; }

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

    /// <summary>Every currently-selected device, for the bulk actions (issue #39) - see <see cref="UpdateSelectedDevices"/>.</summary>
    public IReadOnlyList<DeviceItemViewModel> SelectedDevices => _selectedDevices;

    public int SelectedCount => _selectedDevices.Count;

    public bool HasMultipleSelection => SelectedCount > 1;

    public bool IsSingleSelection => SelectedCount == 1;

    public string SelectedCountText => SelectedCount == 1 ? "1 device selected" : $"{SelectedCount} devices selected";

    /// <summary>Pin (single selection) or Pin all (multiple) - only shown at all when at least one selected device isn't already pinned.</summary>
    public string PinSelectedLabel => HasMultipleSelection ? "Pin all" : "Pin";

    /// <summary>Unpin (single selection) or Unpin all (multiple) - only shown at all when at least one selected device is already pinned.</summary>
    public string UnpinSelectedLabel => HasMultipleSelection ? "Unpin all" : "Unpin";

    public string RediscoverSelectedLabel => HasMultipleSelection ? "Rediscover all" : "Rediscover";

    /// <summary>
    /// True when pinning would do something - at least one selected device
    /// isn't pinned yet. Pin/Unpin show one at a time based on this and
    /// <see cref="ShowUnpinSelectedAction"/> rather than always both, so
    /// picking a fully-pinned (or fully-unpinned) selection doesn't offer an
    /// action that would be a no-op for every device in it. A mixed
    /// selection shows both, since either one still does something.
    /// </summary>
    public bool ShowPinSelectedAction => _selectedDevices.Any(d => !d.IsPinned);

    /// <summary>True when unpinning would do something - see <see cref="ShowPinSelectedAction"/>.</summary>
    public bool ShowUnpinSelectedAction => _selectedDevices.Any(d => d.IsPinned);

    /// <summary>
    /// Forwards the grid's multi-selection from code-behind - DataGrid.SelectedItems
    /// is not a dependency property, so it cannot be bound directly (same
    /// bridging AlertsView.xaml.cs uses for MainViewModel.UpdateSelectedAlerts).
    /// </summary>
    public void UpdateSelectedDevices(IEnumerable<DeviceItemViewModel> devices)
    {
        _selectedDevices.Clear();
        _selectedDevices.AddRange(devices);

        OnPropertyChanged(nameof(SelectedDevices));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(IsSingleSelection));
        OnPropertyChanged(nameof(SelectedCountText));
        RaiseSelectedPinStateChanged();
        PinSelectedCommand.RaiseCanExecuteChanged();
        UnpinSelectedCommand.RaiseCanExecuteChanged();
        AddSelectedToGroupCommand.RaiseCanExecuteChanged();
        RediscoverSelectedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Re-raises everything Pin/Unpin's visibility and labels depend on -
    /// called after the selection itself changes, and after
    /// <see cref="RefreshPinnedState"/> in case a still-selected device's
    /// own pin state changed elsewhere (e.g. the Dashboard's Unpin button).
    /// </summary>
    private void RaiseSelectedPinStateChanged()
    {
        OnPropertyChanged(nameof(PinSelectedLabel));
        OnPropertyChanged(nameof(UnpinSelectedLabel));
        OnPropertyChanged(nameof(RediscoverSelectedLabel));
        OnPropertyChanged(nameof(ShowPinSelectedAction));
        OnPropertyChanged(nameof(ShowUnpinSelectedAction));
    }

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

    /// <summary>Drives the loading/empty/no-matches split on the grid itself (issue #16) - distinct from <see cref="IsBusy"/>, which only covers whether a refresh is in flight after the first load.</summary>
    public ListLoadState LoadState { get; } = new();

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

        _groupMembership.EnsureStarted();

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
            LoadState.CompleteLoad(Devices.Count, VisibleCount);
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
        LoadState.CompleteLoad(Devices.Count, VisibleCount);
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
                existing.IsPinned = _pinnedIds.Contains(device.DeviceId);

                var currentIndex = Devices.IndexOf(existing);
                if (currentIndex >= 0 && currentIndex != target && target < Devices.Count)
                {
                    Devices.Move(currentIndex, target);
                }
            }
            else
            {
                var item = new DeviceItemViewModel(device, nameStyle, TogglePin)
                {
                    IsUnderMaintenance = maintenanceIds.Contains(device.DeviceId),
                    IsPinned = _pinnedIds.Contains(device.DeviceId),
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
    /// both after a device poll and after group membership changes
    /// (<see cref="OnGroupMembershipChanged"/>), since either can change
    /// independently of the other.
    /// </summary>
    private void RebuildGroupFilter(IReadOnlyList<Device> ordered)
    {
        var counts = new Dictionary<string, int>();

        foreach (var device in ordered)
        {
            foreach (var group in GroupsOrNone(device.DeviceId))
            {
                counts[group] = counts.TryGetValue(group, out var existing) ? existing + 1 : 1;
            }
        }

        GroupFilter.Apply(counts.Select(kv => (kv.Key, GroupDisplayText(kv.Key), kv.Value)));
    }

    /// <summary>Forces an immediate re-fetch of group membership rather than waiting for the shared service's own periodic refresh - called by the Groups tab after it creates/edits/deletes a group, so the Group filters (here and on Alerts) don't look stale.</summary>
    public void RequestGroupsRefresh() => _ = _groupMembership.RefreshAsync();

    /// <summary>
    /// Group membership comes from the shared <see cref="IDeviceGroupMembershipService"/>
    /// (also behind the Alerts tab's group filter), fetched separately from
    /// the device poll - rebuild the facet's counts and re-apply the filter
    /// whenever it changes.
    /// </summary>
    private void OnGroupMembershipChanged(object? sender, EventArgs e)
    {
        RebuildGroupFilter(_lastOrderedDevices);
        OnFilterChanged();
    }

    /// <summary>A device's group names, or the single "not in a group" key when it has none - so ungrouped devices are a filterable option of their own.</summary>
    private IReadOnlyList<string> GroupsOrNone(int deviceId) =>
        _groupMembership.GroupsFor(deviceId) is { Count: > 0 } names ? names : NoGroupKey;

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

        // Filtered by device id (the Alerts tab's Device chip), so it matches
        // exactly this device whichever name style either tab is showing.
        // Routed through IWindowService (rather than depending on MainViewModel
        // directly) so the two view models do not depend on each other.
        _windows.ShowAlertsForDevice(device.DeviceId, device.Name);
    }

    /// <summary>
    /// Toggles one device's pin (star icon in the grid's leftmost column) -
    /// see <see cref="SetPinned"/> for what pinning actually does.
    /// </summary>
    private void TogglePin(int deviceId) => SetPinned(deviceId, !_pinnedIds.Contains(deviceId), save: true);

    /// <summary>
    /// Pins or unpins a device. Only changes sort order via <see cref="_pinnedIds"/>
    /// and <see cref="DevicesView"/>'s SortDescriptions - a pinned device
    /// still disappears under the current filters like any other row, it
    /// just sorts first among what remains visible.
    /// </summary>
    /// <param name="save">
    /// False when called in a loop from <see cref="SetSelectedPinned"/>,
    /// which saves once itself after every device in the selection has been
    /// updated, rather than once per device.
    /// </param>
    private void SetPinned(int deviceId, bool pinned, bool save)
    {
        var pinnedDevices = _settings.Current.PinnedDevices;
        var existingIndex = pinnedDevices.FindIndex(p => p.DeviceId == deviceId);

        if (!pinned)
        {
            if (existingIndex >= 0)
            {
                pinnedDevices.RemoveAt(existingIndex);
            }
        }
        else if (existingIndex < 0)
        {
            var name = _index.TryGetValue(deviceId, out var item) ? item.Name : null;
            pinnedDevices.Insert(0, new PinnedDevice { DeviceId = deviceId, DisplayName = name, PinnedAt = DateTimeOffset.Now });
        }

        if (save)
        {
            // Raises Changed, picked up by OnSettingsChanged below - the same
            // reactive path RecordRecentlyViewed relies on for the
            // recently-viewed strip, so a pin toggled from the Dashboard
            // widget's Unpin button shows up here live too.
            _settings.Save();
        }
    }

    /// <summary>Bulk equivalent of <see cref="TogglePin"/> for the current multi-selection (issue #39) - one settings save for the whole batch, not one per device.</summary>
    private void SetSelectedPinned(bool pinned)
    {
        if (_selectedDevices.Count == 0)
        {
            return;
        }

        foreach (var device in _selectedDevices)
        {
            SetPinned(device.DeviceId, pinned, save: false);
        }

        _settings.Save();
    }

    /// <summary>Opens the "Add to group" dialog for the current multi-selection (issue #39).</summary>
    private void AddSelectedToGroup()
    {
        if (_selectedDevices.Count == 0)
        {
            return;
        }

        var deviceIds = _selectedDevices.Select(d => d.DeviceId).ToArray();
        if (_windows.ShowAddDevicesToGroupDialog(deviceIds))
        {
            RequestGroupsRefresh();
        }
    }

    /// <summary>
    /// Triggers a LibreNMS rediscovery for every currently-selected device
    /// (issue #39) - same fire-and-forget request as the single-device
    /// Rediscover action on Device Details (DeviceDetailViewModel.RediscoverAsync),
    /// just concurrently for the whole selection. One device failing does
    /// not stop the others; failures are reported together once every
    /// request has finished.
    /// </summary>
    private async Task RediscoverSelectedAsync()
    {
        if (_selectedDevices.Count == 0)
        {
            return;
        }

        var devices = _selectedDevices.ToList();
        var failedNames = new List<string>();

        await Task.WhenAll(devices.Select(async device =>
        {
            try
            {
                await _client.Devices.DiscoverAsync(device.DeviceId).ConfigureAwait(true);
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not trigger rediscovery for device {DeviceId}", device.DeviceId);
                lock (failedNames)
                {
                    failedNames.Add(device.Name);
                }
            }
        })).ConfigureAwait(true);

        if (failedNames.Count == 0)
        {
            _windows.ShowInformation("Rediscover requested", $"Rediscovery requested for {devices.Count} device(s).");
        }
        else
        {
            _windows.ShowError(
                "Rediscover failed for some devices",
                $"Could not request rediscovery for: {string.Join(", ", failedNames)}");
        }
    }

    private void RefreshPinnedState(IReadOnlyList<PinnedDevice> pinned)
    {
        _pinnedIds = pinned.Select(p => p.DeviceId).ToHashSet();

        foreach (var device in Devices)
        {
            device.IsPinned = _pinnedIds.Contains(device.DeviceId);
        }

        DevicesView.Refresh();
        RaiseSelectedPinStateChanged();
    }

    /// <summary>
    /// Opens the "Add device" dialog and, once it reports a device was
    /// actually added, requests a refresh so the new device shows up without
    /// waiting for the next scheduled poll.
    /// </summary>
    private void AddDevice()
    {
        if (_windows.ShowAddDeviceDialog())
        {
            _deviceMonitor.RequestRefresh();
        }
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

    /// <summary>
    /// Jumped to from a device's own Location link (see DeviceDetailViewModel's
    /// ShowDevicesForLocationCommand): resets every other filter (status
    /// toggles, search text, Type and Group) and isolates the Location facet
    /// down to just this one value, so the Devices tab shows exactly the
    /// devices at that location and nothing left over from whatever was
    /// filtered before.
    /// </summary>
    public void FilterByLocationOnly(string location)
    {
        ShowUp = true;
        ShowDown = true;
        ShowDisabled = true;
        ShowMaintenance = true;
        SearchText = string.Empty;

        TypeFilter.SetAllChecked(true, notify: false);
        GroupFilter.SetAllChecked(true, notify: false);
        LocationFilter.IsolateOne(location, notify: false);

        OnFilterChanged();
    }

    /// <summary>
    /// Jumped to from a device's own Device Groups section (see
    /// DeviceDetailViewModel's ShowDevicesForGroupCommand): same reasoning as
    /// <see cref="FilterByLocationOnly"/>, but isolating the Group facet.
    /// </summary>
    public void FilterByGroupOnly(string groupName)
    {
        ShowUp = true;
        ShowDown = true;
        ShowDisabled = true;
        ShowMaintenance = true;
        SearchText = string.Empty;

        TypeFilter.SetAllChecked(true, notify: false);
        LocationFilter.SetAllChecked(true, notify: false);
        GroupFilter.IsolateOne(groupName, notify: false);

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

        if (!GroupFilter.AllowsAny(GroupsOrNone(device.DeviceId)))
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
        LoadState.UpdateVisibleCount(VisibleCount);
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

        // Opening any Device View saves settings (see
        // DeviceDetailViewModel.RecordRecentlyViewed), so this is how the
        // strip picks up a new entry live rather than only on the next poll.
        RebuildRecentlyViewed(settings.RecentlyViewedDevices);

        RefreshPinnedState(settings.PinnedDevices);
    }

    private void RebuildRecentlyViewed(IReadOnlyList<RecentlyViewedDevice> entries)
    {
        RecentlyViewedDevices.Clear();

        foreach (var entry in entries)
        {
            RecentlyViewedDevices.Add(new RecentlyViewedDeviceItemViewModel(entry, OpenRecentlyViewedDevice));
        }

        OnPropertyChanged(nameof(HasRecentlyViewedDevices));
    }

    private void OpenRecentlyViewedDevice(int deviceId) => _windows.ShowDeviceDetail(deviceId);

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
        _groupMembership.Changed -= OnGroupMembershipChanged;
        _settings.Changed -= OnSettingsChanged;
        _deviceMonitor.PollStarted -= OnPollStarted;
        _deviceMonitor.Polled -= OnPolled;
        TypeFilter.PropertyChanged -= OnFacetPropertyChanged;
        LocationFilter.PropertyChanged -= OnFacetPropertyChanged;
        GroupFilter.PropertyChanged -= OnFacetPropertyChanged;
    }
}
