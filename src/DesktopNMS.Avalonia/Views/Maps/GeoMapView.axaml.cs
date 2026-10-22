using Avalonia.Controls;
using Avalonia.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>Maps › Geographical - bridges the view model's fit and centre requests to the canvas, which owns zoom and pan.</summary>
public partial class GeoMapView : UserControl
{
    private GeoMapViewModel? _viewModel;

    public GeoMapView()
    {
        InitializeComponent();
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

        _viewModel = DataContext as GeoMapViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
            _viewModel.CenterOnRequested += OnCenterOnRequested;
        }
    }

    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(MapCanvas.FitToView, DispatcherPriority.Loaded);

    private void OnCenterOnRequested(object? sender, GeoPin pin) =>
        Dispatcher.UIThread.Post(() => MapCanvas.CenterOn(pin), DispatcherPriority.Loaded);
}
