using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs the "Add/Edit device group" dialog (see
/// <see cref="Views.DeviceGroupEditorWindow"/>). One class serves both modes,
/// same shape as <see cref="ConfirmDialogViewModel"/> serving several
/// notice/confirm variants - create mode is the default; <see cref="Initialize"/>
/// switches it into edit mode for a specific existing (static) group.
/// Only ever builds a static group - see <see cref="DeviceGroup.IsEditableAsStatic"/>.
/// </summary>
public sealed class DeviceGroupEditorViewModel : ObservableObject
{
    private readonly ILibreNmsClient _client;
    private readonly ILogger<DeviceGroupEditorViewModel> _logger;

    /// <summary>Set by <see cref="Initialize"/> before <see cref="LoadDevicesAsync"/> (started from the constructor) gets a chance to run its first await continuation - see that method's remarks.</summary>
    private DeviceGroup? _pendingEditGroup;

    /// <summary>Null in create mode. In edit mode, the group's name *at the time the dialog opened* - PATCH addresses the group by its current name even while <see cref="Name"/> is being changed to a new one.</summary>
    private string? _originalName;

    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _deviceSearchText = string.Empty;
    private bool _isBusy;
    private string? _errorMessage;

    public DeviceGroupEditorViewModel(ILibreNmsClient client, ILogger<DeviceGroupEditorViewModel> logger)
    {
        _client = client;
        _logger = logger;

        DevicePickerItems = new ObservableCollection<DevicePickerItemViewModel>();
        DevicePickerView = CollectionViewSource.GetDefaultView(DevicePickerItems);
        DevicePickerView.Filter = FilterDevicePickerItem;
        DevicePickerView.SortDescriptions.Add(new SortDescription(nameof(DevicePickerItemViewModel.Name), ListSortDirection.Ascending));

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Name));

        _ = LoadDevicesAsync();
    }

    /// <summary>
    /// Switches this dialog into edit mode for an existing static group -
    /// called by <see cref="Services.WindowService.ShowEditDeviceGroupDialog"/>
    /// right after resolving this view model from DI and before the dialog is
    /// shown. Safe to call before <see cref="LoadDevicesAsync"/> has finished:
    /// nothing pumps the message loop (so nothing resumes that method's first
    /// await) until the caller goes on to call ShowDialog, which happens
    /// after this returns.
    /// </summary>
    public void Initialize(DeviceGroup group)
    {
        _pendingEditGroup = group;
        _originalName = group.Name;
        Name = group.Name;
        Description = group.Description ?? string.Empty;
    }

    public bool IsEditMode => _originalName is not null;

    public string Title => IsEditMode ? "Edit device group" : "Add device group";

    public event EventHandler<bool>? RequestClose;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string DeviceSearchText
    {
        get => _deviceSearchText;
        set
        {
            if (SetProperty(ref _deviceSearchText, value))
            {
                DevicePickerView.Refresh();
            }
        }
    }

    public ObservableCollection<DevicePickerItemViewModel> DevicePickerItems { get; }

    public ICollectionView DevicePickerView { get; }

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

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _client.Devices.ListAsync().ConfigureAwait(true);

            IReadOnlyList<int> memberIds = Array.Empty<int>();
            if (_pendingEditGroup is { } group)
            {
                memberIds = await _client.DeviceGroups.GetMemberDeviceIdsAsync(group.Id).ConfigureAwait(true);
            }

            var memberSet = memberIds.ToHashSet();

            DevicePickerItems.Clear();
            foreach (var device in devices)
            {
                DevicePickerItems.Add(new DevicePickerItemViewModel(device, memberSet.Contains(device.DeviceId)));
            }
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load devices for the group editor");
            ErrorMessage = ex.ToUserMessage();
        }
    }

    private async Task SaveAsync()
    {
        ErrorMessage = null;
        IsBusy = true;

        try
        {
            var deviceIds = DevicePickerItems.Where(d => d.IsChecked).Select(d => d.DeviceId).ToArray();
            var description = string.IsNullOrWhiteSpace(Description) ? null : Description;

            if (_originalName is { } currentName)
            {
                await _client.DeviceGroups.UpdateAsync(currentName, Name, description, deviceIds).ConfigureAwait(true);
            }
            else
            {
                await _client.DeviceGroups.CreateAsync(Name, description, deviceIds).ConfigureAwait(true);
            }

            RequestClose?.Invoke(this, true);
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not save device group {GroupName}", Name);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool FilterDevicePickerItem(object item)
    {
        if (item is not DevicePickerItemViewModel device)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(DeviceSearchText)
            || device.Name.Contains(DeviceSearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
