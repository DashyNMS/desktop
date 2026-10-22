using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Dashboard's "Add widget" picker (#204). Opens with the search box
/// focused; Down moves into the cards, arrows move between them, Space ticks
/// one, Enter or a double-click adds, Esc closes.
/// </summary>
public partial class WidgetPickerWindow : Window
{
    private readonly WidgetPickerViewModel? _viewModel;

    /// <summary>For the designer.</summary>
    public WidgetPickerWindow()
    {
        InitializeComponent();
    }

    public WidgetPickerWindow(WidgetPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Opened += (_, _) => SearchBox.Focus();

        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        WidgetList.AddHandler(KeyDownEvent, OnWidgetKeyDown, RoutingStrategies.Tunnel);
        WidgetList.Tapped += OnWidgetTapped;
        WidgetList.DoubleTapped += OnWidgetDoubleTapped;
    }

    /// <summary>True once widgets were added; closing any other way adds nothing.</summary>
    public bool Added { get; private set; }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && WidgetList.ItemCount > 0)
        {
            FocusSelectedCard();
            e.Handled = true;
        }
    }

    /// <summary>Space ticks the focused card; Enter adds what's ticked, or the focused card if nothing is (#277).</summary>
    private void OnWidgetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Space && WidgetList.SelectedItem is WidgetCatalogEntry entry)
        {
            _viewModel.Toggle(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _viewModel.AddChosenOrFocused();
            e.Handled = true;
        }
    }

    /// <summary>A click on a card ticks or unticks it, to add several together.</summary>
    private void OnWidgetTapped(object? sender, TappedEventArgs e)
    {
        if (CardAt(e) is { IsAvailable: true } entry)
        {
            _viewModel?.Toggle(entry);
        }
    }

    /// <summary>Only a double-click on a card, not on the list's empty space.</summary>
    private void OnWidgetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CardAt(e) is { IsAvailable: true } entry)
        {
            _viewModel?.AddNow(entry);
        }
    }

    private static WidgetCatalogEntry? CardAt(RoutedEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as WidgetCatalogEntry;

    private void FocusSelectedCard()
    {
        if (WidgetList.SelectedIndex < 0)
        {
            WidgetList.SelectedIndex = 0;
        }

        WidgetList.ContainerFromIndex(WidgetList.SelectedIndex)?.Focus(NavigationMethod.Directional);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRequestClose(object? sender, bool added)
    {
        _viewModel!.RequestClose -= OnRequestClose;
        Added = added;
        Close();
    }
}
