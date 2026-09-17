using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    private readonly ILibreNmsClient _client;
    private readonly ISessionService _session;
    private readonly IWindowService _windows;
    private readonly DeviceListViewModel _deviceList;
    private readonly ILogger<GroupsViewModel> _logger;

    private bool _hasLoadedOnce;
    private bool _isBusy;
    private string? _errorMessage;
    private string _searchText = string.Empty;

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
        ClearFiltersCommand = new RelayCommand(() => SearchText = string.Empty);
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
                GroupsView.Refresh();
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
                Groups.Add(new GroupListItemViewModel(group, ViewDevices, EditGroup, DeleteGroup));
            }

            OnPropertyChanged(nameof(HasGroups));
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

    private bool FilterGroup(object item)
    {
        if (item is not GroupListItemViewModel group)
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
