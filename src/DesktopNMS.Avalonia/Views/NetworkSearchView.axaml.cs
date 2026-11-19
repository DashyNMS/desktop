using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Network Search page (#57) - see the view model for what it shows.</summary>
public partial class NetworkSearchView : UserControl
{
    private NetworkSearchViewModel? _viewModel;

    public NetworkSearchView()
    {
        InitializeComponent();
    }

    /// <summary>Puts the cursor in the search box with its text selected, ready to type over.</summary>
    public void FocusSearch()
    {
        // After layout: the page may have only just been made visible.
        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested -= OnFocusSearchRequested;
        }

        _viewModel = DataContext as NetworkSearchViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested += OnFocusSearchRequested;
        }

        base.OnDataContextChanged(e);
    }

    private void OnFocusSearchRequested(object? sender, System.EventArgs e) => FocusSearch();

    /// <summary>A double-click anywhere on a row opens it, not just its address.</summary>
    private void OnResultDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => OpenSelected();

    /// <summary>Enter on a selected row opens it, for keyboard users.</summary>
    private void OnResultKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && OpenSelected())
        {
            e.Handled = true;
        }
    }

    private bool OpenSelected()
    {
        if (ResultsGrid.SelectedItem is not EndpointRowViewModel row || !row.OpenCommand.CanExecute(null))
        {
            return false;
        }

        row.OpenCommand.Execute(null);
        return true;
    }
}
