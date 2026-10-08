using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// The Dashboard's "Add widget" picker (#204). Opens with the search box
/// focused; Down moves into the cards, arrows move between them, Enter or a
/// double-click adds one, Esc closes.
/// </summary>
public partial class WidgetPickerWindow : Window
{
    private readonly WidgetPickerViewModel _viewModel;

    public WidgetPickerWindow(WidgetPickerViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && WidgetList.Items.Count > 0)
        {
            FocusSelectedCard();
            e.Handled = true;
        }
    }

    /// <summary>Space ticks the focused card; Enter adds what's ticked, or the focused card if nothing is (#277).</summary>
    private void OnWidgetKeyDown(object sender, KeyEventArgs e)
    {
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
    private void OnWidgetClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(WidgetList, source) is ListBoxItem { DataContext: WidgetCatalogEntry entry })
        {
            _viewModel.Toggle(entry);
        }
    }

    private void OnWidgetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a card, not on the list's empty space.
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(WidgetList, source) is ListBoxItem { DataContext: WidgetCatalogEntry entry })
        {
            _viewModel.AddNow(entry);
        }
    }

    private void FocusSelectedCard()
    {
        if (WidgetList.SelectedItem is null)
        {
            WidgetList.SelectedIndex = 0;
        }

        if (WidgetList.ItemContainerGenerator.ContainerFromItem(WidgetList.SelectedItem) is ListBoxItem item)
        {
            item.Focus();
        }
    }

    private void OnRequestClose(object? sender, bool added)
    {
        _viewModel.RequestClose -= OnRequestClose;
        DialogResult = added;
    }
}
