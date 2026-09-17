using System;
using System.Globalization;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Groups tab - see <see cref="GroupsViewModel"/>.</summary>
public sealed class GroupListItemViewModel : ObservableObject
{
    private int? _memberCount;

    public GroupListItemViewModel(
        DeviceGroup group,
        Action<GroupListItemViewModel> onViewDevices,
        Action<GroupListItemViewModel> onEdit,
        Action<GroupListItemViewModel> onDelete,
        Action<GroupListItemViewModel> onRediscover)
    {
        Group = group;
        ViewDevicesCommand = new RelayCommand(() => onViewDevices(this));
        EditCommand = new RelayCommand(() => onEdit(this), () => IsEditableAsStatic);
        DeleteCommand = new RelayCommand(() => onDelete(this));
        RediscoverCommand = new RelayCommand(() => onRediscover(this));
    }

    public DeviceGroup Group { get; }

    public string Name => Group.Name;

    public string? Description => Group.Description;

    /// <summary>See <see cref="DeviceGroup.IsEditableAsStatic"/> - governs whether Edit is enabled.</summary>
    public bool IsEditableAsStatic => Group.IsEditableAsStatic;

    public string TypeText => Group.Type switch
    {
        { } t when string.Equals(t, "static", StringComparison.OrdinalIgnoreCase) => "Static",
        { } t when string.Equals(t, "dynamic", StringComparison.OrdinalIgnoreCase) => "Dynamic",
        _ => "Unknown",
    };

    /// <summary>A human-readable rendering of <see cref="DeviceGroup.Rules"/> - see <see cref="DeviceGroupRuleFormatter"/>. Null for a static group, or if the rules JSON could not be parsed.</summary>
    public string? Pattern => DeviceGroupRuleFormatter.Format(Group.Rules);

    /// <summary>Null until <see cref="GroupsViewModel"/> resolves it via a separate bounded-concurrency fetch (not part of the initial group list load).</summary>
    public int? MemberCount
    {
        get => _memberCount;
        set
        {
            if (SetProperty(ref _memberCount, value))
            {
                OnPropertyChanged(nameof(MemberCountText));
            }
        }
    }

    public string MemberCountText => MemberCount?.ToString(CultureInfo.InvariantCulture) ?? "...";

    public RelayCommand ViewDevicesCommand { get; }

    public RelayCommand EditCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>Rediscovers every device currently in this group - works for either group type, since it only needs the member device ids, not the rules.</summary>
    public RelayCommand RediscoverCommand { get; }
}
