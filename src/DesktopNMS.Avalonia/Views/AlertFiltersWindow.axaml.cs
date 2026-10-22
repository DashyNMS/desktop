using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Views;

public partial class AlertFiltersWindow : Window
{
    public AlertFiltersWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
