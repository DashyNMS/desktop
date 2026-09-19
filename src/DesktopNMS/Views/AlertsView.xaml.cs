using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

public partial class AlertsView : UserControl
{
    public AlertsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Puts the caret in the search box. Called by the shell window on Ctrl+F.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ApplyGridLayout(GridLayout? layout) => DataGridLayoutHelper.Apply(AlertGrid, layout);

    public GridLayout? CaptureGridLayout() => DataGridLayoutHelper.Capture(AlertGrid);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MainViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Keep a programmatically selected alert (from a toast click, or
        // jumping here from the Devices tab) scrolled into view.
        if (e.PropertyName == nameof(MainViewModel.SelectedAlert)
            && DataContext is MainViewModel { SelectedAlert: { } selected })
        {
            AlertGrid.ScrollIntoView(selected);
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.OpenAlertCommand.CanExecute(null))
        {
            viewModel.OpenAlertCommand.Execute(null);
        }
    }

    /// <summary>
    /// Forwards the grid's multi-selection to the view model.
    /// DataGrid.SelectedItems is not a dependency property, so it cannot be
    /// bound directly - this is the standard way to bridge it into MVVM.
    /// </summary>
    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.UpdateSelectedAlerts(AlertGrid.SelectedItems.Cast<AlertItemViewModel>());
        }
    }

    /// <summary>
    /// Shift-clicking a severity badge isolates that severity instead of
    /// toggling it normally - handled here (rather than a Command) so the
    /// plain click still flips <c>IsChecked</c> via its binding unchanged.
    /// </summary>
    private void SeverityBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        if (sender is not ToggleButton { Tag: AlertSeverity severity } || DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.IsolateSeverity(severity);
        e.Handled = true;
    }

    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
