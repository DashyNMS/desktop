using System;
using System.Globalization;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One row in the Locations tab - see <see cref="LocationsViewModel"/>.</summary>
public sealed class LocationListItemViewModel : ObservableObject
{
    private int? _memberCount;

    public LocationListItemViewModel(
        Location location,
        Action<LocationListItemViewModel> onViewDevices,
        Action<LocationListItemViewModel> onEdit,
        Action<LocationListItemViewModel> onDelete)
    {
        Location = location;
        ViewDevicesCommand = new RelayCommand(() => onViewDevices(this));
        EditCommand = new RelayCommand(() => onEdit(this));
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    public Location Location { get; }

    public string Name => Location.Name;

    public string LatitudeText => Location.Latitude?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-";

    public string LongitudeText => Location.Longitude?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-";

    public string FixedCoordinatesText => Location.FixedCoordinates ? "Yes" : "No";

    /// <summary>Null until <see cref="LocationsViewModel"/> resolves it via a separate pass over the device list (not part of the initial location list load).</summary>
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
}
