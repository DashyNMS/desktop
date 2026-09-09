namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Facts about the LibreNMS schema that DesktopNMS needs in order to present
/// alert faults sensibly.
/// </summary>
/// <remarks>
/// Alert rule queries join <c>devices</c> to whatever table the rule targets and
/// select everything, so a fault row carries the device's entire record
/// alongside the metric that actually tripped. That includes its SNMP
/// credentials.
/// </remarks>
public static class LibreNmsSchema
{
    /// <summary>
    /// Columns that carry credentials. These are dropped outright and never
    /// rendered: an alert pane gets screenshotted, shared and shoulder-surfed,
    /// and no one diagnoses an alert from an SNMP community string.
    /// </summary>
    /// <remarks>
    /// The exact names come from the devices table; the fragment check below
    /// catches equivalents on any other table a rule might join.
    /// </remarks>
    private static readonly HashSet<string> SecretColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "community", "authpass", "authname", "authalgo", "authlevel",
        "cryptopass", "cryptoalgo", "snmp_password", "password", "token",
    };

    private static readonly string[] SecretFragments =
    {
        "pass", "secret", "token", "apikey", "api_key", "privkey",
        "private_key", "community", "credential",
    };

    /// <summary>
    /// devices columns that describe how LibreNMS reaches or catalogues the
    /// device. They say nothing about why an alert fired, and the device is
    /// already named at the top of the pane.
    /// </summary>
    private static readonly HashSet<string> DevicePlumbingColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "device_id", "inserted", "ip", "overwrite_ip", "snmpver", "port", "transport",
        "timeout", "retries", "snmp_disable", "sysObjectID", "sysDescr", "sysContact",
        "location_id", "poller_group", "override_sysLocation", "port_association_mode",
        "max_depth", "disable_notify", "ignore_status", "ignore", "disabled", "icon",
        "serial", "features", "version", "hardware", "os", "purpose", "type", "notes",
        "display_template", "bgpLocalAs", "last_poll_attempted",
        "last_polled_timetaken", "last_discovered_timetaken", "last_discovered",
    };

    /// <summary>
    /// devices columns that can legitimately be the thing a rule tests, e.g. a
    /// "device down" rule keys on status and last_ping. Kept, but ranked below
    /// columns from the table the rule actually targets.
    /// </summary>
    private static readonly HashSet<string> DeviceOperationalColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "status_reason", "uptime", "agent_uptime", "last_polled",
        "last_ping", "last_ping_timetaken", "mtu_status",
    };

    /// <summary>
    /// Columns that name the device itself. These are identity, but weak
    /// identity: the device is already named at the top of the detail pane, so
    /// a fault should be titled by the interface or sensor that faulted, not by
    /// repeating the host.
    /// </summary>
    private static readonly HashSet<string> DeviceIdentityColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "hostname", "sysName", "display",
    };

    /// <summary>Columns that name the faulting entity.</summary>
    private static readonly HashSet<string> IdentityColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "hostname", "sysName", "display", "ifName", "ifDescr", "port_descr_descr",
        "sensor_descr", "storage_descr", "mempool_descr", "processor_descr",
        "diskio_descr", "service_name", "service_desc", "app_type", "name", "label",
    };

    private static readonly string[] DescriptionFragments =
    {
        "descr", "alias", "msg", "message", "comment", "note", "text", "reason",
    };

    /// <summary>True if the column must never be displayed.</summary>
    public static bool IsSecret(string columnName)
    {
        if (SecretColumns.Contains(columnName))
        {
            return true;
        }

        foreach (var fragment in SecretFragments)
        {
            if (columnName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True if the column is device configuration noise rather than signal.</summary>
    public static bool IsDevicePlumbing(string columnName) => DevicePlumbingColumns.Contains(columnName);

    /// <summary>True if the column belongs to the devices table but could be what a rule tests.</summary>
    public static bool IsDeviceOperational(string columnName) => DeviceOperationalColumns.Contains(columnName);

    public static bool IsIdentity(string columnName) => IdentityColumns.Contains(columnName);

    /// <summary>True if the column names the device rather than the thing that faulted.</summary>
    public static bool IsDeviceIdentity(string columnName) => DeviceIdentityColumns.Contains(columnName);

    public static bool IsDescription(string columnName)
    {
        foreach (var fragment in DescriptionFragments)
        {
            if (columnName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
