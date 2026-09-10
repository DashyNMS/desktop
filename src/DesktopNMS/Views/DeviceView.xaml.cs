using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// A single device's detail window, opened from the Devices tab (double-click
/// or the row's "device view" action) instead of jumping straight to the
/// LibreNMS website. Non-modal - see <see cref="Services.WindowService.ShowDeviceDetail"/> -
/// so several can be open alongside the main window and each other.
/// </summary>
public partial class DeviceView : Window
{
    public DeviceView(DeviceDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
