using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Map tab (issue #56). Bridges the view model's fit/centre requests to
/// the canvas (which owns zoom and pan) and the canvas's drag/double-click
/// back to the view model.
/// </summary>
public partial class NetworkMapView : UserControl
{
    private NetworkMapViewModel? _viewModel;

    public NetworkMapView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        MapCanvas.NodeMoved += (_, node) => _viewModel?.OnNodeMoved(node);
        MapCanvas.NodeActivated += (_, node) => _viewModel?.OpenDevice(node);
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

        _viewModel = e.NewValue as NetworkMapViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
            _viewModel.CenterOnRequested += OnCenterOnRequested;
        }
    }

    // Deferred to Loaded priority so the canvas has the new Nodes (bound)
    // and a real size before fitting against them.
    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MapCanvas.FitToView);

    private void OnCenterOnRequested(object? sender, MapNode node) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MapCanvas.CenterOn(node));
}
