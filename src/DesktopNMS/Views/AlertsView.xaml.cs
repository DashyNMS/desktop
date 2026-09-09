using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
}
