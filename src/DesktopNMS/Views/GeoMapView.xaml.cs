using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Maps tab's Geographical map - bridges the view model's fit/centre requests to the canvas, which owns zoom and pan.</summary>
public partial class GeoMapView : UserControl
{
    private GeoMapViewModel? _viewModel;

    public GeoMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested -= OnFitToViewRequested;
            _viewModel.CenterOnRequested -= OnCenterOnRequested;
        }

        _viewModel = e.NewValue as GeoMapViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
            _viewModel.CenterOnRequested += OnCenterOnRequested;
        }
    }

    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MapCanvas.FitToView);

    private void OnCenterOnRequested(object? sender, GeoPin pin) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MapCanvas.CenterOn(pin));
}
