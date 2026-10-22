using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views.DeviceDetail;

/// <summary>
/// Device Details' Unimus backups (#115): the grid's selection drives the
/// viewer - one backup views it, two are compared (DeviceDetailViewModel.
/// OnConfigSelectionChanged) - and the line viewer jumps to changes, with the
/// overview ruler beside it.
/// </summary>
public partial class UnimusSection : UserControl
{
    /// <summary>Rows left above a change when jumping to it, so it doesn't sit against the top edge.</summary>
    private const int JumpContext = 3;

    private DeviceDetailViewModel? _viewModel;
    private bool _suppressSelectionForward;
    private double _previousOffset;
    private double? _pendingOffset;
    private int? _pendingRow;

    public UnimusSection()
    {
        InitializeComponent();

        BackupsGrid.SelectionChanged += (_, _) =>
        {
            if (!_suppressSelectionForward)
            {
                ForwardSelection();
            }
        };

        // A tick-box click toggles its row in or out of the selection, like
        // ctrl-click - seen tunnelling, before the row would single-select itself.
        BackupsGrid.AddHandler(PointerPressedEvent, OnBackupsPointerPressed, RoutingStrategies.Tunnel);

        LinesScroll.ScrollChanged += (_, _) => UpdateRuler();
        DiffRuler.NavigateRequested += (_, fraction) =>
        {
            // Centre the clicked point rather than putting it at the top edge.
            var target = (fraction * LinesScroll.Extent.Height) - (LinesScroll.Viewport.Height / 2);
            LinesScroll.Offset = new Vector(LinesScroll.Offset.X, Math.Max(0, target));
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.ScrollToConfigLineRequested -= OnScrollToLineRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DeviceDetailViewModel;
        _previousOffset = 0;
        _pendingOffset = null;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToConfigLineRequested += OnScrollToLineRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void ForwardSelection() =>
        _viewModel?.OnConfigSelectionChanged(BackupsGrid.SelectedItems.OfType<UnimusBackupItemViewModel>().ToList());

    private void OnBackupsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(BackupsGrid).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is not { DataContext: UnimusBackupItemViewModel item } tick
            || !tick.Classes.Contains("tick"))
        {
            return;
        }

        e.Handled = true;
        var desired = BackupsGrid.SelectedItems.OfType<UnimusBackupItemViewModel>().ToList();
        if (!desired.Remove(item))
        {
            desired.Add(item);
        }

        _suppressSelectionForward = true;
        BackupsGrid.SelectedItems.Clear();
        foreach (var backup in desired)
        {
            BackupsGrid.SelectedItems.Add(backup);
        }

        _suppressSelectionForward = false;
        ForwardSelection();
    }

    /// <summary>New lines start at the top - unless the view model jumps straight on to a change, or puts the view back where it was.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceDetailViewModel.ConfigLines))
        {
            _previousOffset = LinesScroll.Offset.Y;
            ScheduleScroll(0);
        }
    }

    /// <summary>A change to jump to (a row), or null to put the view back where it was (a hidden run expanded).</summary>
    private void OnScrollToLineRequested(int? row)
    {
        if (row is not { } target)
        {
            ScheduleScroll(_previousOffset);
            return;
        }

        ScheduleScroll(0, byRow: target);
    }

    /// <summary>
    /// Scrolls once the new rows are laid out - straight away would act on the
    /// old lines. Requests in one pass collapse into the last, so "back to the
    /// top" for new lines gives way to a jump to their first change.
    /// </summary>
    private void ScheduleScroll(double offset, int? byRow = null)
    {
        var alreadyScheduled = _pendingOffset is not null;
        _pendingOffset = offset;
        _pendingRow = byRow;
        if (alreadyScheduled)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingOffset is { } target)
            {
                // A row's offset is worked out here, with the new lines' extent known: rows are
                // all one height but for the odd collapsed run, so it's the row's share of the whole.
                if (_pendingRow is { } row && _viewModel?.ConfigLines is { Count: > 0 } lines)
                {
                    target = Math.Max(0, row - JumpContext) * LinesScroll.Extent.Height / lines.Count;
                }

                LinesScroll.Offset = new Vector(0, target);
            }

            _pendingOffset = null;
            _pendingRow = null;
        }, DispatcherPriority.Loaded);
    }

    private void UpdateRuler()
    {
        var extent = LinesScroll.Extent.Height;
        if (extent > 0)
        {
            DiffRuler.ViewportStart = LinesScroll.Offset.Y / extent;
            DiffRuler.ViewportSize = LinesScroll.Viewport.Height / extent;
        }
    }

    /// <summary>A collapsed "N unchanged lines hidden" row expands when clicked; other rows ignore it.</summary>
    private void OnLinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: UnimusDiffLineViewModel { IsHidden: true } line } && _viewModel is { } viewModel)
        {
            viewModel.ExpandHiddenLinesCommand.Execute(line);
        }
    }
}
