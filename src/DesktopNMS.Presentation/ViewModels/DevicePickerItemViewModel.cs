using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One checkable row in the device group editor's device picker - see <see cref="DeviceGroupEditorViewModel"/>.</summary>
public sealed class DevicePickerItemViewModel : ObservableObject
{
    private bool _isChecked;

    public DevicePickerItemViewModel(Device device, bool isChecked)
    {
        DeviceId = device.DeviceId;
        Name = device.BestName;
        _isChecked = isChecked;
    }

    public int DeviceId { get; }

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
