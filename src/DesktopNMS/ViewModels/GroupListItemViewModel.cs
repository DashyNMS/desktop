using System;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Groups tab - see <see cref="GroupsViewModel"/>.</summary>
public sealed class GroupListItemViewModel
{
    public GroupListItemViewModel(
        DeviceGroup group,
        Action<GroupListItemViewModel> onViewDevices,
        Action<GroupListItemViewModel> onEdit,
        Action<GroupListItemViewModel> onDelete)
    {
        Group = group;
        ViewDevicesCommand = new RelayCommand(() => onViewDevices(this));
        EditCommand = new RelayCommand(() => onEdit(this), () => IsEditableAsStatic);
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public DeviceGroup Group { get; }

    public string Name => Group.Name;

    public string? Description => Group.Description;

    /// <summary>See <see cref="DeviceGroup.IsEditableAsStatic"/> - governs whether Edit is enabled and whether the "Dynamic" badge shows.</summary>
    public bool IsEditableAsStatic => Group.IsEditableAsStatic;

    public RelayCommand ViewDevicesCommand { get; }

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }
}
