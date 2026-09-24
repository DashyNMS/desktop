using System.Windows;
using System.Windows.Controls;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>How a device is added, apart from its hostname - see <see cref="DeviceAddOptionsViewModel"/>.</summary>
public partial class DeviceAddOptionsView : UserControl
{
    public DeviceAddOptionsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox does not expose a bindable password, by design, so the
    /// value is pushed to the view model here instead - same as
    /// ConnectionWindow's API token field.
    /// </summary>
    private void OnAuthPassChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceAddOptionsViewModel options)
        {
            options.AuthPass = AuthPassBox.Password;
        }
    }

    private void OnCryptoPassChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceAddOptionsViewModel options)
        {
            options.CryptoPass = CryptoPassBox.Password;
        }
    }
}
