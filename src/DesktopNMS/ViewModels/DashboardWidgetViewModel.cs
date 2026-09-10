using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A widget placed on the Dashboard's grid. Holds the chrome everything
/// shares - title, grid position/span, remove, and a per-widget "editing"
/// state (see <see cref="IsEditingWidget"/>) - while a subclass supplies the
/// type-specific content (e.g. <see cref="SensorWidgetViewModel"/>).
/// <see cref="Column"/>/<see cref="Row"/>/<see cref="ColumnSpan"/>/
/// <see cref="RowSpan"/> change locally while the user drags/resizes, live
/// reflowing other widgets out of the way; the view commits the whole
/// affected set in one shot once the gesture ends (see
/// <see cref="DashboardViewModel.CommitLayout"/>), since a drag can move more
/// than just this one widget. The title commits immediately since it is
/// edited via a text box rather than a drag.
/// </summary>
public abstract class DashboardWidgetViewModel : ObservableObject
{
    private readonly IDashboardLayoutService _layout;
    private string _title;
    private int _column;
    private int _row;
    private int _columnSpan;
    private int _rowSpan;
    private bool _isEditingWidget;

    protected DashboardWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model)
    {
        _layout = layout;
        Id = model.Id;
        _title = model.Title;
        _column = model.Column;
        _row = model.Row;
        _columnSpan = model.ColumnSpan;
        _rowSpan = model.RowSpan;

        RemoveCommand = new RelayCommand(() => _layout.RemoveWidget(Id));
        ToggleEditCommand = new RelayCommand(() => IsEditingWidget = !IsEditingWidget);
    }

    /// <summary>For subclasses that need to call other layout operations scoped to this widget's <see cref="Id"/>.</summary>
    protected IDashboardLayoutService Layout => _layout;

    public string Id { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                _layout.Rename(Id, value);
            }
        }
    }

    /// <summary>0-based grid column of the widget's left edge. Updated live while dragging.</summary>
    public int Column
    {
        get => _column;
        set => SetProperty(ref _column, value);
    }

    /// <summary>0-based grid row of the widget's top edge. Updated live while dragging.</summary>
    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    /// <summary>Width in grid cells. Updated live while resizing.</summary>
    public int ColumnSpan
    {
        get => _columnSpan;
        set => SetProperty(ref _columnSpan, value);
    }

    /// <summary>Height in grid cells. Updated live while resizing.</summary>
    public int RowSpan
    {
        get => _rowSpan;
        set => SetProperty(ref _rowSpan, value);
    }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ToggleEditCommand { get; }

    /// <summary>
    /// True while this widget's own edit icon is toggled on: its title becomes
    /// renameable and its remove button appears. Independent of the
    /// Dashboard-wide "Edit layout" mode, which only governs dragging/resizing.
    /// </summary>
    public bool IsEditingWidget
    {
        get => _isEditingWidget;
        set
        {
            if (SetProperty(ref _isEditingWidget, value) && !value)
            {
                OnEditingClosed();
            }
        }
    }

    /// <summary>Lets a subclass reset any edit-only state (e.g. an open picker) when editing closes.</summary>
    protected virtual void OnEditingClosed()
    {
    }

    /// <summary>Refreshes the local copies from the layout's model, e.g. after an external change.</summary>
    public virtual void SyncFrom(DashboardWidget model)
    {
        Title = model.Title;
        Column = model.Column;
        Row = model.Row;
        ColumnSpan = model.ColumnSpan;
        RowSpan = model.RowSpan;
    }
}
