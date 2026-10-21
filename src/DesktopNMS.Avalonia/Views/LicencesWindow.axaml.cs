using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Views;

public partial class LicencesWindow : Window
{
    public LicencesWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
