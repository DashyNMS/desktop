using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>Graylog messages - see <see cref="GraylogMessagesViewModel"/>.</summary>
public partial class GraylogMessagesView : UserControl
{
    public GraylogMessagesView()
    {
        InitializeComponent();

        MessagesGrid.DoubleTapped += OnGridDoubleTapped;

        // A message selected from elsewhere (a dashboard widget click, #304) is scrolled to.
        MessagesGrid.SelectionChanged += (_, _) =>
        {
            if (MessagesGrid.SelectedItem is { } item)
            {
                MessagesGrid.ScrollIntoView(item, null);
            }
        };
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Double-clicking a message opens the device it came from, when it's a known one - only on the Logs tab, where it's a different device each row.</summary>
    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not GraylogMessagesViewModel { IsFleet: true } viewModel
            || (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { DataContext: GraylogMessageItemViewModel item })
        {
            return;
        }

        if (viewModel.OpenDeviceCommand.CanExecute(item))
        {
            viewModel.OpenDeviceCommand.Execute(item);
            e.Handled = true;
        }
    }
}
