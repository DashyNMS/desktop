using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.Help;

public partial class HelpView : UserControl
{
    private HelpViewModel? _model;

    public HelpView()
    {
        InitializeComponent();
        PageView.ResolveLink = target => _model?.Resolve(target) ?? new DesktopNMS.Core.Help.HelpBrokenLink(target);
        PageView.ResolveImage = target => _model?.Image(target);
        PageView.LinkActivated += (_, target) => _model?.Activate(target);
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Navigated -= OnNavigated;
            _model.SearchFocusRequested -= OnSearchFocusRequested;
        }

        _model = DataContext as HelpViewModel;
        if (_model is not null)
        {
            _model.Navigated += OnNavigated;
            _model.SearchFocusRequested += OnSearchFocusRequested;
        }
    }

    /// <summary>Alt+Left and Alt+Right as in a browser (Cmd+[ and Cmd+] on macOS), Escape out of a search.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _model is null)
        {
            return;
        }

        var back = (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.OemOpenBrackets && e.KeyModifiers == KeyModifiers.Meta);
        var forward = (e.Key == Key.Right && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.OemCloseBrackets && e.KeyModifiers == KeyModifiers.Meta);
        var command = back ? _model.BackCommand : forward ? _model.ForwardCommand : null;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _model.IsSearching)
        {
            _model.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) => FocusSearch();

    /// <summary>Once the page is laid out: its heading at the top, and focused so a screen reader starts there.</summary>
    private void OnNavigated(object? sender, string? anchor) => Dispatcher.UIThread.Post(() =>
    {
        if (PageView.FindHeading(anchor) is not { } heading)
        {
            return;
        }

        var top = anchor is null ? null : heading.TranslatePoint(default, PageView);
        PageScroller.Offset = new Vector(0, top is { } point ? Math.Max(0, point.Y + PageView.Margin.Top - 12) : 0);

        // Not while someone is typing a search.
        if (!SearchBox.IsFocused && IsEffectivelyVisible)
        {
            heading.Focus(NavigationMethod.Unspecified);
        }
    }, DispatcherPriority.Background);
}
