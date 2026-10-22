using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AlertsView : UserControl
{
    private MainViewModel? _viewModel;

    public AlertsView()
    {
        InitializeComponent();

        AlertGrid.SelectionChanged += OnGridSelectionChanged;
        AlertGrid.DoubleTapped += OnGridDoubleTapped;

        // Tunnelling, so a shift-click on a severity count is seen before it toggles.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>Keeps an alert selected from elsewhere - a toast, the Devices tab - in view.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAlert) && _viewModel?.SelectedAlert is { } selected)
        {
            AlertGrid.ScrollIntoView(selected, null);
        }
    }

    /// <summary>The grid's several selected rows, which can't be bound, passed to the view model.</summary>
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => _viewModel?.UpdateSelectedAlerts(AlertGrid.SelectedItems.OfType<AlertItemViewModel>());

    /// <summary>
    /// Opens the device in this app (#158): the alert itself is already in the
    /// detail pane. Only a double-click on a row, not its header or details.
    /// </summary>
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null
            && (e.Source as Visual)?.FindAncestorOfType<DataGridDetailsPresenter>(includeSelf: true) is null
            && _viewModel?.OpenDeviceCommand.CanExecute(null) == true)
        {
            _viewModel.OpenDeviceCommand.Execute(null);
        }
    }

    private void OnAlertMenuOpening(object? sender, CancelEventArgs e) => _viewModel?.RefreshNotificationMenu();

    /// <summary>Shift-clicking a severity count shows only that severity instead of toggling it.</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Shift
            && _viewModel is not null
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: AlertSeverity severity } badge
            && badge.Classes.Contains("severity"))
        {
            _viewModel.IsolateSeverity(severity);
            e.Handled = true;
        }
    }
}
