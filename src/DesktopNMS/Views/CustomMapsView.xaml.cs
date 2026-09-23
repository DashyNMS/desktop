using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Maps tab's Custom Maps - connects the canvas's clicks and drags to the view model, and fit requests back to the canvas.</summary>
public partial class CustomMapsView : UserControl
{
    private CustomMapsViewModel? _viewModel;

    public CustomMapsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        MapCanvas.BackgroundClicked += (_, p) => _viewModel?.OnBackgroundClicked(p.X, p.Y);
        MapCanvas.NodeClicked += (_, id) => _viewModel?.OnNodeClicked(id);
        MapCanvas.NodeActivated += (_, id) => _viewModel?.OnNodeActivated(id);
        MapCanvas.EdgeClicked += (_, id) => _viewModel?.OnEdgeClicked(id);
        MapCanvas.Moved += (_, _) => _viewModel?.OnMoved();
        MapCanvas.KeyDown += OnCanvasKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested -= OnFitToViewRequested;
        }

        _viewModel = e.NewValue as CustomMapsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
        }
    }

    // Deferred so the canvas has the new map (bound) before fitting to it.
    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, MapCanvas.FitToView);

    private void OnFitClick(object sender, RoutedEventArgs e) => MapCanvas.FitToView();

    /// <summary>Delete removes the selected node or link while editing - only while the map itself has focus, so it never eats Delete in a text box.</summary>
    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _viewModel?.DeleteSelectionCommand.CanExecute(null) == true)
        {
            _viewModel.DeleteSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
