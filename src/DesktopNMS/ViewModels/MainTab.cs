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

    /// <summary>Every access point the switches see over LLDP (#55) - a sub-tab of Devices, like Groups and Locations.</summary>
    AccessPoints,
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
