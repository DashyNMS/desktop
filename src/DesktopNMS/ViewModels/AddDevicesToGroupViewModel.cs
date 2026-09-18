using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add to group" dialog opened from a multi-selection on the
/// Devices grid (issue #39). Only lists static (explicit device-list)
/// groups - see <see cref="DeviceGroup.IsEditableAsStatic"/> - since a
/// dynamic group's membership comes from its own rules, not a device list
/// this app could add to. Saving unions the selected devices into the
/// chosen group's existing members (LibreNMS's own group update is a full
/// replace, not incremental - see <see cref="IDeviceGroupsApi.UpdateAsync"/>),
/// so devices already in the group are left alone rather than duplicated
/// or dropped.
/// </summary>
public sealed class AddDevicesToGroupViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ILogger<AddDevicesToGroupViewModel> _logger;

    private IReadOnlyList<int> _deviceIds = Array.Empty<int>();
    private DeviceGroup? _selectedGroup;
    private bool _isBusy;
    private bool _isLoading = true;
    private string? _errorMessage;

    public AddDevicesToGroupViewModel(ILibreNmsClient client, ILogger<AddDevicesToGroupViewModel> logger)
    {
        _client = client;
        _logger = logger;

        StaticGroups = new ObservableCollection<DeviceGroup>();

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && SelectedGroup is not null);
    }

    /// <summary>
    /// Called by <see cref="Services.WindowService.ShowAddDevicesToGroupDialog"/>
    /// right after resolving this view model from DI and before the dialog
    /// is shown - same convention as <see cref="DeviceGroupEditorViewModel.Initialize"/>.
    /// </summary>
    public void Initialize(IReadOnlyList<int> deviceIds)
    {
        _deviceIds = deviceIds;
        _ = LoadGroupsAsync();
    }

    public string DeviceCountText => _deviceIds.Count == 1 ? "1 device" : $"{_deviceIds.Count} devices";

    public string IntroText =>
        $"Adds {DeviceCountText} to the group you pick below - devices already in it are left alone. " +
        "Only static (explicit device-list) groups can be picked - a dynamic group's membership comes from its own rules.";

    public event EventHandler<bool>? RequestClose;

    public ObservableCollection<DeviceGroup> StaticGroups { get; }

    public DeviceGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand SaveCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
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

    public bool ShowNoStaticGroupsMessage => !IsLoading && !HasError && StaticGroups.Count == 0;

    private async Task LoadGroupsAsync()
    {
        try
        {
            var groups = await _client.DeviceGroups.ListAsync().ConfigureAwait(true);

            StaticGroups.Clear();
            foreach (var group in groups.Where(g => g.IsEditableAsStatic).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                StaticGroups.Add(group);
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load device groups for the add-to-group dialog");
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowNoStaticGroupsMessage));
        }
    }

    private async Task SaveAsync()
    {
        if (SelectedGroup is not { } group)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;

        try
        {
            var existingIds = await _client.DeviceGroups.GetMemberDeviceIdsAsync(group.Id).ConfigureAwait(true);
            var mergedIds = existingIds.Union(_deviceIds).ToArray();

            await _client.DeviceGroups.UpdateAsync(group.Name, group.Name, group.Description, mergedIds).ConfigureAwait(true);

            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not add devices to group {GroupName}", group.Name);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }
}
