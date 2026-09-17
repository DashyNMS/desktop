using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// View model behind the Groups tab: lists every LibreNMS device group and
/// lets static ones be created/edited/deleted - see
/// <see cref="Core.Models.DeviceGroup.IsEditableAsStatic"/> for why dynamic
/// groups stay read-only (view/delete only) here. Unlike
/// <see cref="DeviceListViewModel"/>, this has no background poll monitor:
/// group membership only ever changes through this app's own actions or the
/// LibreNMS web UI, far less often than device state, so a manual/lazy load
/// is enough.
/// </summary>
public sealed class GroupsViewModel : ObservableObject
{
    /// <summary>Caps concurrent per-device DiscoverAsync calls when rediscovering a whole group - same reasoning as DeviceGroupsApi's own membership-fetch concurrency cap.</summary>
    private const int RediscoverConcurrency = 6;

    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly DeviceListViewModel _deviceList;
    private readonly ILogger<GroupsViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private bool _showStatic = true;
    private bool _showDynamic = true;

    public GroupsViewModel(
        ILibreNmsClient client,
        ISessionService session,
        IWindowService windows,
        DeviceListViewModel deviceList,
        ILogger<GroupsViewModel> logger)
    {
        _client = client;
        _session = session;
        _windows = windows;
        _deviceList = deviceList;
        _logger = logger;

        Groups = new ObservableCollection<GroupListItemViewModel>();
        GroupsView = CollectionViewSource.GetDefaultView(Groups);
        GroupsView.Filter = FilterGroup;
        GroupsView.SortDescriptions.Add(new SortDescription(nameof(GroupListItemViewModel.Name), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _session.IsConnected && !IsBusy);
        AddGroupCommand = new RelayCommand(AddGroup);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<GroupListItemViewModel> Groups { get; }

    public ICollectionView GroupsView { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand AddGroupCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool ShowStatic
    {
        get => _showStatic;
        set
        {
            if (SetProperty(ref _showStatic, value))
            {
                OnFilterChanged();
            }
        }
    }

    public bool ShowDynamic
    {
        get => _showDynamic;
        set
        {
            if (SetProperty(ref _showDynamic, value))
            {
                OnFilterChanged();
            }
        }
    }

    public int StaticCount => Groups.Count(g => g.TypeText == "Static");

    public int DynamicCount => Groups.Count(g => g.TypeText == "Dynamic");

    /// <summary>Drives whether the Clear button does anything - broader than just search text, same reasoning as DeviceListViewModel's own property of this name.</summary>
    public bool HasAnyFilterApplied => !string.IsNullOrEmpty(SearchText) || !ShowStatic || !ShowDynamic;

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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasGroups => Groups.Count > 0;

    /// <summary>Loads the group list once, lazily, the first time the tab is actually shown - same convention as every other tab.</summary>
    public void OnShown()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var groups = await _client.DeviceGroups.ListAsync().ConfigureAwait(true);

            Groups.Clear();
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                Groups.Add(new GroupListItemViewModel(group, ViewDevices, EditGroup, DeleteGroup, RediscoverGroup));
            }

            OnPropertyChanged(nameof(HasGroups));
            OnPropertyChanged(nameof(StaticCount));
            OnPropertyChanged(nameof(DynamicCount));

            // A second pass, not part of the row above: one GetMemberDeviceIdsAsync
            // call per group (bounded concurrency), so the initial list render
            // is not held up waiting on it. Non-fatal on failure - the Devices
            // column just stays at "..." rather than failing the whole load.
            try
            {
                var counts = await _client.DeviceGroups.GetMemberCountsAsync(groups).ConfigureAwait(true);
                foreach (var item in Groups)
                {
                    if (counts.TryGetValue(item.Group.Id, out var count))
                    {
                        item.MemberCount = count;
                    }
                }
            }
            catch (LibreNmsApiException ex)
            {
                _logger.LogWarning(ex, "Could not load device group member counts");
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load device groups");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddGroup()
    {
        if (_windows.ShowAddDeviceGroupDialog())
        {
            _ = LoadAsync();
            _deviceList.RequestGroupsRefresh();
        }
    }

    private void EditGroup(GroupListItemViewModel item)
    {
        if (!item.IsEditableAsStatic)
        {
            return;
        }

        if (_windows.ShowEditDeviceGroupDialog(item.Group))
        {
            _ = LoadAsync();
            _deviceList.RequestGroupsRefresh();
        }
    }

    private void ViewDevices(GroupListItemViewModel item) => _windows.ShowDevicesFilteredByGroup(item.Name);

    private void DeleteGroup(GroupListItemViewModel item) => _ = DeleteGroupAsync(item);

    private async Task DeleteGroupAsync(GroupListItemViewModel item)
    {
        var message = item.IsEditableAsStatic
            ? $"Permanently delete the group '{item.Name}' from LibreNMS? This cannot be undone."
            : $"Permanently delete the group '{item.Name}' from LibreNMS? This cannot be undone, and its rules cannot be recreated from DashyNMS - only from LibreNMS's own web UI.";

        if (!_windows.Confirm("Delete group", message))
        {
            return;
        }

        try
        {
            await _client.DeviceGroups.DeleteAsync(item.Name).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            _deviceList.RequestGroupsRefresh();
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not delete device group {GroupName}", item.Name);
            _windows.ShowError("Delete failed", ex.ToUserMessage());
        }
    }

    private void RediscoverGroup(GroupListItemViewModel item) => _ = RediscoverGroupAsync(item);

    /// <summary>
    /// Queues an on-demand rediscovery (see <see cref="IDevicesApi.DiscoverAsync"/>)
    /// for every device currently in this group - works for either group
    /// type, since it only needs the member device ids, not the rules.
    /// </summary>
    private async Task RediscoverGroupAsync(GroupListItemViewModel item)
    {
        IReadOnlyList<int> deviceIds;

        try
        {
            deviceIds = await _client.DeviceGroups.GetMemberDeviceIdsAsync(item.Group.Id).ConfigureAwait(true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load members to rediscover group {GroupName}", item.Name);
            _windows.ShowError("Rediscover group", ex.ToUserMessage());
            return;
        }

        if (deviceIds.Count == 0)
        {
            _windows.ShowInformation("Rediscover group", $"'{item.Name}' has no devices to rediscover.");
            return;
        }

        if (!_windows.Confirm("Rediscover group", $"Ask LibreNMS to rediscover all {deviceIds.Count} device(s) in '{item.Name}' now?"))
        {
            return;
        }

        var failures = 0;

        using (var gate = new SemaphoreSlim(RediscoverConcurrency))
        {
            await Task.WhenAll(deviceIds.Select(async deviceId =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _client.Devices.DiscoverAsync(deviceId).ConfigureAwait(false);
                }
                catch (LibreNmsApiException ex)
                {
                    Interlocked.Increment(ref failures);
                    _logger.LogWarning(ex, "Could not trigger rediscovery for device {DeviceId} in group {GroupName}", deviceId, item.Name);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(true);
        }

        if (failures == 0)
        {
            _windows.ShowInformation("Rediscover requested", $"Rediscovery queued for all {deviceIds.Count} device(s) in '{item.Name}'.");
        }
        else
        {
            _windows.ShowError("Rediscover group", $"Queued for {deviceIds.Count - failures} of {deviceIds.Count} device(s) - {failures} failed. Check the log for details.");
        }
    }

    private void ClearFilters()
    {
        _showStatic = true;
        _showDynamic = true;
        _searchText = string.Empty;

        OnPropertyChanged(nameof(ShowStatic));
        OnPropertyChanged(nameof(ShowDynamic));
        OnPropertyChanged(nameof(SearchText));
        OnFilterChanged();
    }

    /// <summary>Shift-click on a type badge: show only that type, hiding the other.</summary>
    public void IsolateType(bool isStatic)
    {
        _showStatic = isStatic;
        _showDynamic = !isStatic;

        OnPropertyChanged(nameof(ShowStatic));
        OnPropertyChanged(nameof(ShowDynamic));
        OnFilterChanged();
    }

    private void OnFilterChanged()
    {
        GroupsView.Refresh();
        OnPropertyChanged(nameof(HasAnyFilterApplied));
    }

    private bool FilterGroup(object item)
    {
        if (item is not GroupListItemViewModel group)
        {
            return false;
        }

        var typeAllowed = group.TypeText switch
        {
            "Static" => ShowStatic,
            "Dynamic" => ShowDynamic,
            _ => true,
        };

        if (!typeAllowed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var term = SearchText.Trim();
        return group.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (group.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
