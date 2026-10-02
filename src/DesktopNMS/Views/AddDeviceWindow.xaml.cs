using System;
using System.Windows;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AddDeviceWindow : Window
{
    private readonly AddDeviceViewModel _viewModel;

    public AddDeviceWindow(AddDeviceViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        _viewModel.RequestBulkAdd += OnRequestBulkAdd;

        Loaded += (_, _) => HostnameBox.Focus();
    }

    /// <summary>True when closed via "Add several..." - the caller opens Bulk add devices in its place.</summary>
    public bool SwitchToBulkAdd { get; private set; }

    private void OnRequestClose(object? sender, bool added)
    {
        Unsubscribe();
        DialogResult = added;
    }

    private void OnRequestBulkAdd(object? sender, EventArgs e)
    {
        Unsubscribe();
        SwitchToBulkAdd = true;
        DialogResult = false;
    }

    private void Unsubscribe()
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.RequestBulkAdd -= OnRequestBulkAdd;
    }
}
