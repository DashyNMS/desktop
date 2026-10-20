namespace DesktopNMS.ViewModels;

/// <summary>Which section of the device window is showing.</summary>
public enum DeviceDetailSection
{
    Overview,
    Sensors,
    Ports,
    Resources,
    Vlans,
    Fdb,
    Arp,
    Graphs,

    /// <summary>Both this device's currently active alerts and its historical alert log - see <see cref="Views.DeviceView"/>.</summary>
    Alerts,
    EventLog,

    /// <summary>Editable fields plus device-management actions (Rediscover now, Delete eventually) - a home for "change this device" rather than "view its data".</summary>
    Edit,

    /// <summary>Config backups via Unimus (issue #115) - only meaningful once the integration is set up in Settings.</summary>
    Config,

    /// <summary>This device's Graylog messages (issue #114) - only shown once Graylog is set up in Settings.</summary>
    Graylog,

    /// <summary>ENTITY-MIB physical inventory (#164) - chassis, slots, power supplies, fans, modules.</summary>
    Inventory,

    /// <summary>BGP sessions, OSPF neighbours and VRFs (#53).</summary>
    Routing,

    /// <summary>Wireless readings (#55) - AP and client counts, signal, noise, ...</summary>
    Wireless,

    /// <summary>What the device is connected to, over LLDP/CDP from either side, matched to LibreNMS devices.</summary>
    Neighbours,
}
