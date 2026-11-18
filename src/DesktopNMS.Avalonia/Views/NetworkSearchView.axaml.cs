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
}
