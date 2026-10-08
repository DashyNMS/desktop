using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>What a widget's ⋯ menu asks the dashboard to do - the dashboard owns Undo, the set-up panel and the grid (#274-#279).</summary>
internal interface IWidgetActions
{
    void SetUp(DashboardWidgetViewModel widget);

    void Remove(DashboardWidgetViewModel widget);

    void Duplicate(DashboardWidgetViewModel widget);

    void Resize(DashboardWidgetViewModel widget, WidgetSize size);
}

/// <summary>
/// A widget placed on the Dashboard's grid. Holds the chrome everything
/// shares - title, grid position/span, and the ⋯ menu's Set up…, Rename,
/// Size, Duplicate and Remove (#274) - while a subclass supplies the
/// type-specific content (e.g. <see cref="SensorWidgetViewModel"/>).
/// <see cref="Column"/>/<see cref="Row"/>/<see cref="ColumnSpan"/>/
/// <see cref="RowSpan"/> change locally while the user drags/resizes, live
/// reflowing other widgets out of the way; the view commits the whole
/// affected set in one shot once the gesture ends (see
/// <see cref="DashboardViewModel.CommitLayout"/>), since a drag can move more
/// than just this one widget.
/// </summary>
public abstract class DashboardWidgetViewModel : ObservableObject
{
    /// <summary>The kinds a dashboard can hold several of - and so the ones Duplicate is offered for (#279).</summary>
    private static readonly HashSet<string> Several = new(StringComparer.Ordinal)
    {
        DashboardWidgetTypes.Sensors, DashboardWidgetTypes.Graph, DashboardWidgetTypes.TopInterfaces, DashboardWidgetTypes.TopErrors,
        DashboardWidgetTypes.TopDevices, DashboardWidgetTypes.EventLog, DashboardWidgetTypes.Graylog,
    };

    private readonly IDashboardLayoutService _layout;
    private string _title;
    private int _column;
    private int _row;
    private int _columnSpan;
    private int _rowSpan;
    private bool _isEditingWidget;
    private bool _isRenaming;
    private bool _isSelected;
    private bool _isHighlighted;

    protected DashboardWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model)
    {
        _layout = layout;
        Id = model.Id;
        WidgetType = model.WidgetType;
        _title = model.Title;
        _column = model.Column;
        _row = model.Row;
        _columnSpan = model.ColumnSpan;
        _rowSpan = model.RowSpan;

        SetUpCommand = new RelayCommand(() => Actions?.SetUp(this));
        RemoveCommand = new RelayCommand(() =>
        {
            if (Actions is { } actions)
            {
                actions.Remove(this);
            }
            else
            {
                _layout.RemoveWidget(Id);
            }
        });
        DuplicateCommand = new RelayCommand(() => Actions?.Duplicate(this), () => AllowsSeveral);
        RenameCommand = new RelayCommand(() => IsRenaming = true);
        FinishRenameCommand = new RelayCommand(() => IsRenaming = false);
        SetSizeCommand = new RelayCommand(parameter =>
        {
            if (parameter is WidgetSize size || (parameter is string text && Enum.TryParse(text, out size)))
            {
                Actions?.Resize(this, size);
            }
        });
    }

    /// <summary>Set by the dashboard: where the ⋯ menu's actions go.</summary>
    internal IWidgetActions? Actions { get; set; }

    /// <summary>For subclasses that need to call other layout operations scoped to this widget's <see cref="Id"/>.</summary>
    protected IDashboardLayoutService Layout => _layout;

    public string Id { get; }

    /// <summary>The <see cref="DashboardWidget.WidgetType"/> - which kind of widget this is.</summary>
    public string WidgetType { get; }

    /// <summary>A dashboard can hold several of this kind, so it can be duplicated (#279).</summary>
    public bool AllowsSeveral => Several.Contains(WidgetType);

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
        set
        {
            if (SetProperty(ref _columnSpan, value))
            {
                OnPropertyChanged(nameof(CurrentSize));
            }
        }
    }

    /// <summary>Height in grid cells. Updated live while resizing.</summary>
    public int RowSpan
    {
        get => _rowSpan;
        set
        {
            if (SetProperty(ref _rowSpan, value))
            {
                OnPropertyChanged(nameof(CurrentSize));
            }
        }
    }

    /// <summary>Which of the sizes it is now, if any - ticked in the Size menu (#278).</summary>
    public WidgetSize? CurrentSize => DashboardGrid.SizeOf(ColumnSpan, RowSpan);

    /// <summary>Briefly true after the widget is added or duplicated, so it stands out where it landed (#204).</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        private set => SetProperty(ref _isHighlighted, value);
    }

    /// <summary>Highlights the widget for a moment and a half.</summary>
    public void Flash()
    {
        IsHighlighted = true;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsHighlighted = false;
        };
        timer.Start();
    }

    /// <summary>Widget-specific controls for the title bar - templated by type, and shown in the set-up panel too. Null for a widget with none.</summary>
    public virtual object? HeaderOptions => null;

    public RelayCommand SetUpCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand DuplicateCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand FinishRenameCommand { get; }

    /// <summary>Size ▸ Small, Medium, Large or Wide (#278).</summary>
    public RelayCommand SetSizeCommand { get; }

    /// <summary>The title is a text box: ⋯ › Rename, or F2.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetProperty(ref _isRenaming, value);
    }

    /// <summary>The widget the keys act on in edit mode - outlined (#278).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// True while this widget is open in the set-up panel (#275): it's marked
    /// "Live preview", and a subclass's set-up state (an open picker) is
    /// reset once it closes.
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

    /// <summary>Lets a subclass reset any set-up-only state (e.g. an open picker) when the panel closes.</summary>
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
