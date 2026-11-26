using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Views;

/// <summary>Network Search's filters (#57) - see the view for what each tab filters.</summary>
public partial class NetworkSearchFiltersWindow : Window
{
    public NetworkSearchFiltersWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
