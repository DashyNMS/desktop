namespace DesktopNMS.ViewModels;

/// <summary>Which section of the main window is showing.</summary>
public enum MainTab
{
    Dashboard,
    Devices,
    Health,
    Alerts,
    Groups,
    Locations,
    Rules,
    Templates,

    /// <summary>The network map (issue #56).</summary>
    Map,
}
