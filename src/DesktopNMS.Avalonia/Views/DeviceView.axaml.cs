using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// A single device's detail window (#250, from the WPF app's DeviceView),
/// opened from the Devices tab and elsewhere. Non-modal, so several can be
/// open alongside the main window - see WindowService.ShowDeviceDetail.
/// </summary>
public partial class DeviceView : Window
{
    /// <summary>The sections ported so far; any other shows "Not here yet".</summary>
    private static readonly HashSet<DeviceDetailSection> PortedSections =
    [
        DeviceDetailSection.Overview,
        DeviceDetailSection.Neighbours,
        DeviceDetailSection.Sensors,
        DeviceDetailSection.Ports,
        DeviceDetailSection.Vlans,
        DeviceDetailSection.Fdb,
        DeviceDetailSection.Arp,
    ];

    private DeviceDetailViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public DeviceView()
    {
        InitializeComponent();
    }

    public DeviceView(DeviceDetailViewModel viewModel, ISettingsStore settings)
    {
        InitializeComponent();
        WindowPlacementMemory.Attach(this, settings);
        TitleBarFit.Attach(this, TitleBarContent);

        // Back and forward (#58): Alt+Left/Right and the mouse's own buttons.
        AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnNavigationPointerPressed, RoutingStrategies.Tunnel);

        ShowViewModel(viewModel);
    }

    /// <summary>
    /// Shows another device in this window (#58): the window and its size stay;
    /// everything about the device comes from the new view model. The host
    /// disposes the old one.
    /// </summary>
    public void ShowViewModel(DeviceDetailViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateNotYetSection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceDetailViewModel.SelectedSection))
        {
            UpdateNotYetSection();
        }
    }

    private void UpdateNotYetSection() =>
        NotYetSection.IsVisible = _viewModel is { } viewModel && !PortedSections.Contains(viewModel.SelectedSection);

    private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || _viewModel?.History is not { } history)
        {
            return;
        }

        if (e.Key == Key.Left && history.BackCommand.CanExecute(null))
        {
            history.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && history.ForwardCommand.CanExecute(null))
        {
            history.ForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNavigationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.History is not { } history)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsXButton1Pressed && history.BackCommand.CanExecute(null))
        {
            history.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (point.IsXButton2Pressed && history.ForwardCommand.CanExecute(null))
        {
            history.ForwardCommand.Execute(null);
            e.Handled = true;
        }
    }
}
