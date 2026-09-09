using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A widget placed on the Dashboard's free-form canvas. Holds the chrome
/// everything shares - title, position, size, remove, and a per-widget
/// "editing" state (see <see cref="IsEditingWidget"/>) - while a subclass
/// supplies the type-specific content (e.g. <see cref="SensorWidgetViewModel"/>).
/// Position/size change locally as the user drags/resizes (see
/// <see cref="CommitPosition"/>/<see cref="CommitSize"/>); the title commits
/// immediately since it is edited via a text box rather than a drag.
/// </summary>
public abstract class DashboardWidgetViewModel : ObservableObject
{
    private readonly IDashboardLayoutService _layout;
    private string _title;
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private bool _isEditingWidget;

    protected DashboardWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model)
    {
        _layout = layout;
        Id = model.Id;
        _title = model.Title;
        _x = model.X;
        _y = model.Y;
        _width = model.Width;
        _height = model.Height;

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

    /// <summary>Canvas.Left. Updated live while dragging; see <see cref="CommitPosition"/>.</summary>
    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    /// <summary>Canvas.Top. Updated live while dragging; see <see cref="CommitPosition"/>.</summary>
    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
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

    /// <summary>Persists the current position. Call once a drag finishes, not on every delta.</summary>
    public void CommitPosition() => _layout.Move(Id, X, Y);

    /// <summary>Persists the current size. Call once a resize finishes, not on every delta.</summary>
    public void CommitSize() => _layout.Resize(Id, Width, Height);

    /// <summary>Refreshes the local copies from the layout's model, e.g. after an external change.</summary>
    public virtual void SyncFrom(DashboardWidget model)
    {
        Title = model.Title;
        X = model.X;
        Y = model.Y;
        Width = model.Width;
        Height = model.Height;
    }
}
