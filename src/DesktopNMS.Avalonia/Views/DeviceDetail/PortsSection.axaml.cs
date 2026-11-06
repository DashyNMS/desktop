using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// Device Details' Ports: grouped by the chosen key (#272) - a view concern,
/// on the grid's own view of the ports, so search and column sorting still
/// work inside each group - and a port picked from elsewhere scrolled to, in
/// the table or among the cards (#569). The cards come ready-made in rows
/// from <see cref="DeviceDetailViewModel.PortCardRows"/>, shown in a
/// virtualising list (#572); this view says how many fit across and keeps
/// Tab moving through them in order.
/// </summary>
public partial class PortsSection : UserControl
{
    /// <summary>The narrowest a card gets before a row drops to fewer cards (#569).</summary>
    public const double CardMinWidth = 320;

    /// <summary>The gap between cards in a row.</summary>
    public const double CardSpacing = 12;

    private DeviceDetailViewModel? _viewModel;
    private DataGridColumn? _sortColumn;
    private ListSortDirection _sortDirection;

    public PortsSection()
    {
        InitializeComponent();
        PortsGrid.Sorting += OnGridSorting;
        PortsGrid.Loaded += (_, _) => ApplyGrouping();
        PortCardsList.SizeChanged += (_, e) => UpdateCardColumns(e.NewSize.Width);
        PortCardsList.KeyDown += OnCardsKeyDown;
        PortCardsList.GotFocus += OnCardsGotFocus;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DeviceDetailViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _sortColumn = null;
        ApplyGrouping();
        UpdateCardColumns(PortCardsList.Bounds.Width);
    }

    /// <summary>
    /// Tells the cards how many fit across. The list sits in a scroller that
    /// never scrolls sideways, so its width is the width the cards share.
    /// Only a change in the count regroups the rows, so resizing within one
    /// count costs nothing.
    /// </summary>
    private void UpdateCardColumns(double width)
    {
        if (_viewModel is not null && width > 0)
        {
            _viewModel.PortCards.Columns = PortCardRowLayout.ColumnsFor(width, CardMinWidth, CardSpacing);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceDetailViewModel.PortGroupProperty))
        {
            ApplyGrouping();
        }
        else if (e.PropertyName == nameof(DeviceDetailViewModel.SelectedPort) && _viewModel?.SelectedPort is { } port)
        {
            if (_viewModel.IsPortCardLayout)
            {
                BringCardIntoView(port);
            }
            else
            {
                PortsGrid.ScrollIntoView(port, null);
            }
        }
        else if (e.PropertyName == nameof(DeviceDetailViewModel.PortCardGroups) && _viewModel?.SelectedPort is { } picked)
        {
            // Switching to cards, or a reload, keeps the picked port in sight.
            BringCardIntoView(picked);
        }
    }

    /// <summary>
    /// Scrolls the picked port's card into view - after layout, since a port
    /// picked from a Top widget or a fresh card list has no card on screen
    /// yet. The list is virtualised, so the card may not exist at all until
    /// its row is scrolled to; the row is found from the cards, not the screen.
    /// </summary>
    private void BringCardIntoView(PortItemViewModel port) => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is not { IsPortCardLayout: true })
        {
            return;
        }

        var row = PortCardRowLayout.RowOf(_viewModel.PortCardRows, port);
        if (row >= 0)
        {
            PortCardsList.ScrollIntoView(row);
        }
    }, DispatcherPriority.Loaded);

    /// <summary>
    /// Tab and Shift+Tab move card by card in reading order. The default
    /// order follows the panel's children, which a virtualising panel leaves
    /// out of order once rows are recycled, and it can't reach a card whose
    /// row isn't built. At either end the key is left alone, and the list's
    /// TabNavigation of Once takes focus out of the list.
    /// </summary>
    private void OnCardsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != 0
            || _viewModel is null || CardOf(e.Source) is not { } card)
        {
            return;
        }

        var forward = (e.KeyModifiers & KeyModifiers.Shift) == 0;
        if (PortCardRowLayout.Next(_viewModel.PortCardRows, card.Port, forward) is { } next)
        {
            e.Handled = true;
            FocusCard(next, e.KeyModifiers);
        }
    }

    /// <summary>
    /// Tabbing into the list lands on its first card, or with Shift on its
    /// last, as it did before the list was virtualised - not on whichever card
    /// last had focus, which Once would otherwise pick.
    /// </summary>
    private void OnCardsGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // From another card is a move within the list, even if that card's
        // row has just been recycled out of the list's visual tree.
        if (e.NavigationMethod != NavigationMethod.Tab || _viewModel is null || CardOf(e.OldFocusedElement) is not null
            || (e.OldFocusedElement is Visual old && PortCardsList.IsVisualAncestorOf(old)))
        {
            return;
        }

        var rows = _viewModel.PortCardRows;
        var modifiers = e.KeyModifiers;
        var target = (modifiers & KeyModifiers.Shift) == 0 ? PortCardRowLayout.First(rows) : PortCardRowLayout.Last(rows);
        if (target is { } position && !ReferenceEquals(CardOf(e.Source), position.Card))
        {
            // Not from inside the focus change that is still under way.
            Dispatcher.UIThread.Post(() => FocusCard(position, modifiers), DispatcherPriority.Loaded);
        }
    }

    /// <summary>Scrolls a card's row into view, building it if need be, and focuses the card.</summary>
    private void FocusCard(PortCardPosition position, KeyModifiers modifiers)
    {
        PortCardsList.ScrollIntoView(position.Row);
        var button = PortCardsList.ContainerFromIndex(position.Row)?
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, position.Card));
        button?.Focus(NavigationMethod.Tab, modifiers);
    }

    /// <summary>The card a key or focus event came from, if any.</summary>
    private static PortCard? CardOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("portCard"))?.DataContext as PortCard;

    /// <summary>Groups by the chosen key, the groups in their natural order - Up before Down, VLANs and speeds by number.</summary>
    private void ApplyGrouping()
    {
        if (_viewModel is null || PortsGrid.CollectionView is not DataGridCollectionView view)
        {
            return;
        }

        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (_viewModel.PortGroupProperty is { } property)
            {
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(property));
            }

            ApplySort(view);
        }
    }

    /// <summary>A column click sorts within the groups, which keep their own order.</summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Column.SortMemberPath) || PortsGrid.CollectionView is not DataGridCollectionView view)
        {
            return;
        }

        e.Handled = true;
        _sortDirection = ReferenceEquals(_sortColumn, e.Column) && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortColumn = e.Column;

        using (view.DeferRefresh())
        {
            ApplySort(view);
        }
    }

    private void ApplySort(DataGridCollectionView view)
    {
        view.SortDescriptions.Clear();
        if (_viewModel?.PortGroupProperty is { } property)
        {
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new GroupOrder(_viewModel.PortGroupingValue, property)));
        }

        if (_sortColumn is { SortMemberPath: { Length: > 0 } path })
        {
            view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new PropertyComparer(path), _sortDirection));
        }
    }

    /// <summary>Orders ports by their group's place: Up before Down, VLANs and speeds by number, types by name.</summary>
    private sealed class GroupOrder(PortGrouping grouping, string property) : IComparer
    {
        private readonly PropertyComparer _byName = new(property);

        public int Compare(object? x, object? y)
        {
            if (x is not PortItemViewModel a || y is not PortItemViewModel b)
            {
                return 0;
            }

            var byKey = a.GroupSortKey(grouping).CompareTo(b.GroupSortKey(grouping));
            return byKey != 0 ? byKey : _byName.Compare(a, b);
        }
    }
}
