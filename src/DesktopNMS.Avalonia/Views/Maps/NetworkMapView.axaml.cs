using Avalonia.Controls;
using Avalonia.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>
/// Maps › Network (#56). Bridges the view model's fit and centre requests to
/// the canvas (which owns zoom and pan), and the canvas's drag and
/// double-click back to the view model.
/// </summary>
public partial class NetworkMapView : UserControl
{
    private NetworkMapViewModel? _viewModel;

    public NetworkMapView()
    {
        InitializeComponent();
        MapCanvas.NodeMoved += (_, node) => _viewModel?.OnNodeMoved(node);
        MapCanvas.NodeActivated += (_, node) => _viewModel?.OpenDevice(node);
    }

    /// <summary>Puts the caret in the find box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested -= OnFitToViewRequested;
            _viewModel.CenterOnRequested -= OnCenterOnRequested;
        }

        _viewModel = DataContext as NetworkMapViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
            _viewModel.CenterOnRequested += OnCenterOnRequested;
        }
    }

    // Once laid out, so the canvas has the new nodes (bound) and a real size to fit them in.
    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(MapCanvas.FitToView, DispatcherPriority.Loaded);

    private void OnCenterOnRequested(object? sender, MapNode node) =>
        Dispatcher.UIThread.Post(() => MapCanvas.CenterOn(node), DispatcherPriority.Loaded);
}
