using Avalonia.Controls;
using Avalonia.Interactivity;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AddDeviceWindow : Window
{
    private readonly AddDeviceViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public AddDeviceWindow()
    {
        InitializeComponent();
    }

    public AddDeviceWindow(AddDeviceViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        viewModel.RequestBulkAdd += OnRequestBulkAdd;
        Opened += (_, _) => HostnameBox.Focus();
        Closed += (_, _) => Unsubscribe();
    }

    /// <summary>True once a device was added.</summary>
    public bool Added { get; private set; }

    /// <summary>True when closed with "Add several…" - the caller opens Bulk add devices in its place.</summary>
    public bool SwitchToBulkAdd { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool added)
    {
        Added = added;
        Close();
    }

    private void OnRequestBulkAdd(object? sender, EventArgs e)
    {
        SwitchToBulkAdd = true;
        Close();
    }

    private void Unsubscribe()
    {
        if (_viewModel is not null)
        {
            _viewModel.RequestClose -= OnRequestClose;
            _viewModel.RequestBulkAdd -= OnRequestBulkAdd;
        }
    }
}
