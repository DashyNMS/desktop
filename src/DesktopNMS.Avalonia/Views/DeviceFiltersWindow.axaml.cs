using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Views;

public partial class DeviceFiltersWindow : Window
{
    public DeviceFiltersWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
