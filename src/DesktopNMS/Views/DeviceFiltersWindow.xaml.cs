using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class DeviceFiltersWindow : Window
{
    public DeviceFiltersWindow(DeviceListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
