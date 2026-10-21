using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DesktopNMS.Views;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        InitializeComponent();
    }

    private void OnGotIt(object? sender, RoutedEventArgs e) => Close();
}
