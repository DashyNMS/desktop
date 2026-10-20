using DesktopNMS.Core.Configuration;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A widget whose type this version doesn't know - added by DashyNMS Mobile
/// or a newer desktop version. Shown as what it is rather than as an empty
/// widget of some other kind, and its stored settings are left alone so it
/// still works wherever it was made (#196). It can still be moved, resized
/// and removed like any other widget.
/// </summary>
public sealed class UnsupportedWidgetViewModel : DashboardWidgetViewModel
{
    public UnsupportedWidgetViewModel(IDashboardLayoutService layout, DashboardWidget model)
        : base(layout, model)
    {
        WidgetType = model.WidgetType;
    }

    public string WidgetType { get; }

    public string Message => "This widget isn't supported in this version of DashyNMS.";
}
