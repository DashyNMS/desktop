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

    /// <summary>The user's views of what the switches see over LLDP/CDP (#55) - its own tab, with a hover menu of views.</summary>
    Neighbours,
    Rules,
    Templates,

    /// <summary>Maps → Network: the LLDP/CDP topology map (issue #56).</summary>
    MapsNetwork,

    /// <summary>Maps → Geographical: locations as pins on a real map.</summary>
    MapsGeographical,

    /// <summary>Maps → Custom Maps - empty for now.</summary>
    MapsCustom,

    /// <summary>Logs → Graylog: every device's Graylog messages (issue #114).</summary>
    LogsGraylog,
}
