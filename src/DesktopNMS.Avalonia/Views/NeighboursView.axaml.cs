using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>The Neighbours tab - see <see cref="NeighboursViewModel"/>.</summary>
public partial class NeighboursView : UserControl
{
    public NeighboursView()
    {
        InitializeComponent();

        // Tunnelling, so a shift-click on a state count is seen before it toggles,
        // and a right-click on a column header before the grid takes it.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Puts the caret in the search box (Ctrl+F).</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Visual;

        // Shift-clicking a state count shows only that state instead of toggling it.
        if (e.KeyModifiers == KeyModifiers.Shift
            && DataContext is NeighboursViewModel viewModel
            && source?.FindAncestorOfType<ToggleButton>(includeSelf: true) is { Tag: NeighbourState state } badge
            && badge.Classes.Contains("state"))
        {
            viewModel.IsolateState(state);
            e.Handled = true;
            return;
        }

        // Right-clicking a column header offers every column to show or hide, as on the Devices tab.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed
            && source?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is { } header
            && header.FindAncestorOfType<DataGrid>() == NeighboursGrid)
        {
            e.Handled = true;
            ShowColumnsMenu(header);
        }
    }

    private void ShowColumnsMenu(Control header)
    {
        var items = new List<MenuItem>();
        foreach (var column in NeighboursGrid.Columns.Where(c => c.Header is string { Length: > 0 }))
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = (string)column.Header! },
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
            };

            item.Click += (_, _) =>
            {
                // Never hide the last column showing.
                if (column.IsVisible && NeighboursGrid.Columns.Count(c => c.IsVisible) <= 1)
                {
                    item.IsChecked = true;
                    return;
                }

                column.IsVisible = !column.IsVisible;
                item.IsChecked = column.IsVisible;
            };

            items.Add(item);
        }

        new ContextMenu { ItemsSource = items }.Open(header);
    }

    /// <summary>A neighbourhood's own menu: edit, show on the map, move, delete. "All" has none.</summary>
    private void OnChipContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control { DataContext: NeighbourhoodChipViewModel { IsNeighbourhood: true } chip } control
            || DataContext is not NeighboursViewModel viewModel)
        {
            return;
        }

        var showOnMap = new MenuItem
        {
            Header = new TextBlock { Text = "Show on network map" },
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = chip.ShowsOnMap,
            Command = viewModel.ToggleShowOnMapCommand,
            CommandParameter = chip,
        };

        new ContextMenu
        {
            ItemsSource = new Control[]
            {
                new MenuItem { Header = new TextBlock { Text = "Edit…" }, Command = viewModel.EditViewCommand, CommandParameter = chip },
                showOnMap,
                new Separator(),
                new MenuItem { Header = new TextBlock { Text = "Move left" }, IsEnabled = chip.CanMoveLeft, Command = viewModel.MoveLeftCommand, CommandParameter = chip },
                new MenuItem { Header = new TextBlock { Text = "Move right" }, IsEnabled = chip.CanMoveRight, Command = viewModel.MoveRightCommand, CommandParameter = chip },
                new Separator(),
                new MenuItem
                {
                    Header = new TextBlock { Text = "Delete", Foreground = this.TryFindResource("CriticalBrush", ActualThemeVariant, out var red) ? red as IBrush : null },
                    Command = viewModel.DeleteViewCommand,
                    CommandParameter = chip,
                },
            },
        }.Open(control);
    }
}
