using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Maps;

/// <summary>A heading row in the custom maps list: a menu group's name.</summary>
public sealed record MapGroupHeading(string Label);

/// <summary>
/// Maps › Custom - connects the canvas's clicks and drags to the view model,
/// and fit requests back to the canvas. Avalonia's ListBox has no grouping,
/// so the maps list is built here with a heading row before each menu group.
/// </summary>
public partial class CustomMapsView : UserControl
{
    private readonly ObservableCollection<object> _rows = new();
    private CustomMapsViewModel? _viewModel;
    private bool _syncingSelection;

    public CustomMapsView()
    {
        InitializeComponent();

        MapList.ItemsSource = _rows;
        MapList.ContainerPrepared += (_, e) => e.Container.Classes.Set("heading", e.Container.DataContext is MapGroupHeading);
        MapList.SelectionChanged += (_, _) =>
        {
            if (_syncingSelection || _viewModel is null)
            {
                return;
            }

            if (MapList.SelectedItem is CustomMapSummary summary)
            {
                _viewModel.SelectedSummary = summary;
            }

            // The change may have been refused (unsaved edits) or landed on a heading.
            SyncSelection();
        };

        MapCanvas.BackgroundClicked += (_, p) => _viewModel?.OnBackgroundClicked(p.X, p.Y);
        MapCanvas.NodeClicked += (_, id) => _viewModel?.OnNodeClicked(id);
        MapCanvas.NodeActivated += (_, id) => _viewModel?.OnNodeActivated(id);
        MapCanvas.EdgeClicked += (_, id) => _viewModel?.OnEdgeClicked(id);
        MapCanvas.Moved += (_, _) => _viewModel?.OnMoved();
        MapCanvas.KeyDown += OnCanvasKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested -= OnFitToViewRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ((INotifyCollectionChanged)_viewModel.MapsView).CollectionChanged -= OnMapsChanged;
        }

        _viewModel = DataContext as CustomMapsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.FitToViewRequested += OnFitToViewRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ((INotifyCollectionChanged)_viewModel.MapsView).CollectionChanged += OnMapsChanged;
        }

        RebuildList();
    }

    private void OnMapsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildList();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomMapsViewModel.SelectedSummary))
        {
            SyncSelection();
        }
    }

    /// <summary>The maps in their menu groups, a heading before each; ungrouped maps first, as LibreNMS lists them.</summary>
    private void RebuildList()
    {
        var maps = _viewModel?.MapsView.OfType<CustomMapSummary>().ToList() ?? [];
        var groups = maps.GroupBy(m => string.IsNullOrWhiteSpace(m.MenuGroup) ? null : m.MenuGroup.Trim())
            .OrderBy(g => g.Key is not null)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

        _syncingSelection = true;
        _rows.Clear();
        foreach (var group in groups)
        {
            _rows.Add(new MapGroupHeading((group.Key ?? "Ungrouped").ToUpperInvariant()));
            foreach (var map in group)
            {
                _rows.Add(map);
            }
        }

        _syncingSelection = false;
        SyncSelection();
    }

    private void SyncSelection()
    {
        _syncingSelection = true;
        MapList.SelectedItem = _viewModel?.SelectedSummary is { } summary && _rows.Contains(summary) ? summary : null;
        _syncingSelection = false;
    }

    // Once laid out, so the canvas has the new map (bound) before fitting to it.
    private void OnFitToViewRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(MapCanvas.FitToView, DispatcherPriority.Loaded);

    private void OnFitClick(object? sender, RoutedEventArgs e) => MapCanvas.FitToView();

    /// <summary>Delete removes the selected node or link while editing - only while the map itself has focus, so it never eats Delete in a text box.</summary>
    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _viewModel?.DeleteSelectionCommand.CanExecute(null) == true)
        {
            _viewModel.DeleteSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }
}
