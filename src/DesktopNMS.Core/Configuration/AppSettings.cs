using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Configuration;

/// <summary>
/// Which tab to show when DashyNMS starts. Mirrors DesktopNMS.ViewModels.MainTab,
/// duplicated here rather than referenced so this Core project does not depend
/// on the WPF view-model project.
/// </summary>
public enum StartupTab
{
    Dashboard,
    Devices,
    Health,
    Alerts,
    Groups,
    Locations,
}

/// <summary>
/// The base colour palette the app is styled from - see Themes/Palette.Dark.xaml
/// and Themes/Palette.Light.xaml. Changing this needs a restart to take
/// effect (unlike <see cref="AppSettings.AccentColor"/>, which applies live) -
/// WPF resolves the styles built from it once, when they are first parsed.
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Everything DesktopNMS persists between runs, apart from the API token which
/// is encrypted separately by <see cref="Security.ITokenProtector"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Hard ceiling on <see cref="RecentlyViewedDeviceCount"/> itself, independent of whatever the user picks.</summary>
    public const int MaxRecentlyViewedDeviceCount = 25;

    /// <summary>Root URL of the LibreNMS web UI, e.g. https://nms.example.com/.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Accept self-signed or internally-issued certificates.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How often to refresh, in seconds. Alerts polls continuously in the
    /// background at this interval; Devices and Health use it too, but only
    /// once each has been shown at least once (see AutoRefreshTimer).
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Keep the API token on this machine (DPAPI-encrypted) so sign-in is not needed each launch.</summary>
    public bool RememberToken { get; set; } = true;

    /// <summary>
    /// True if the LibreNMS server stores alert timestamps in UTC. Most installs
    /// use server-local time, which is the default here.
    /// </summary>
    public bool ServerTimestampsAreUtc { get; set; }

    /// <summary>Also fetch alerts in state 0 (recovered) so they can be shown and can raise recovery toasts.</summary>
    public bool IncludeRecoveredAlerts { get; set; }

    /// <summary>Which device name to show in the alert list.</summary>
    public DeviceNameStyle DeviceNameStyle { get; set; } = DeviceNameStyle.Hostname;

    /// <summary>
    /// Fetch the faults (the rows the rule matched) for the selected alert.
    /// Costs one extra request per selection, so it can be turned off.
    /// </summary>
    public bool LoadAlertFaults { get; set; } = true;

    /// <summary>Closing the window hides it to the notification area instead of exiting.</summary>
    public bool MinimiseToTrayOnClose { get; set; } = true;

    /// <summary>Start hidden in the notification area.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>Register a Run key so DesktopNMS starts at sign-in.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Which tab is showing when the main window first appears.</summary>
    public StartupTab StartupTab { get; set; } = StartupTab.Alerts;

    /// <summary>
    /// The newest release tag DashyNMS has already shown an update toast for,
    /// so the same "update available" notice does not repeat on every launch.
    /// </summary>
    public string? LastNotifiedUpdateVersion { get; set; }

    /// <summary>
    /// Also consider GitHub pre-release ("preview") builds - rollups of
    /// in-progress work published between stable releases - when checking
    /// for updates, both for the Settings &gt; About check and the
    /// background startup check. Off by default: most users should only
    /// ever be nudged towards a stable release.
    /// </summary>
    public bool IncludePreviewBuilds { get; set; }

    /// <summary>
    /// Skip the confirmation prompt before a large bulk alert acknowledge/
    /// unacknowledge (see MainViewModel's bulk-confirm threshold). Set when
    /// the user ticks "don't ask me again" on that prompt; re-enabled from
    /// Settings &gt; Alert display.
    /// </summary>
    public bool SuppressBulkAlertActionConfirmation { get; set; }

    public NotificationSettings Notifications { get; set; } = new();

    public AlertFilterSettings Filter { get; set; } = new();

    /// <summary>Unimus config-backup integration (issue #115) - see <see cref="UnimusSettings"/>. The API token itself is encrypted separately by <see cref="Security.IUnimusTokenProtector"/>, the same split as the main LibreNMS token.</summary>
    public UnimusSettings Unimus { get; set; } = new();

    /// <summary>Graylog log integration (issue #114) - see <see cref="GraylogSettings"/>. The password itself is encrypted separately by <see cref="Security.IGraylogPasswordProtector"/>, the same split as the Unimus token.</summary>
    public GraylogSettings Graylog { get; set; } = new();

    /// <summary>Warning/critical bands applied to dBm sensors on the Health tab.</summary>
    public DbmThresholdSettings DbmThresholds { get; set; } = new();

    /// <summary>Warning/critical bands applied to "signal" sensors on the Health tab.</summary>
    public SignalThresholdSettings SignalThresholds { get; set; } = new();

    /// <summary>Warning/critical bands applied to temperature sensors on the Health tab.</summary>
    public BandThresholdSettings TemperatureThresholds { get; set; } = BandThresholdSettings.TemperatureDefaults();

    /// <summary>Warning/critical bands applied to fan-speed sensors on the Health tab.</summary>
    public BandThresholdSettings FanSpeedThresholds { get; set; } = BandThresholdSettings.FanSpeedDefaults();

    /// <summary>
    /// When a sensor has its own warning/critical limit configured in
    /// LibreNMS, that normally wins over the threshold below it for whichever
    /// bound it specifies (a device-specific limit is more accurate than one
    /// fleet-wide guess). Turning this on reverts to always using the
    /// thresholds below, ignoring what is configured on the sensor itself.
    /// </summary>
    public bool OverrideSensorLimitsWithAppThresholds { get; set; }

    /// <summary>Widgets laid out on the Dashboard tab (grid position/span, title, type, and - for a Sensors widget - which sensors it shows).</summary>
    public List<DashboardWidget> DashboardWidgets { get; set; } = new();

    /// <summary>
    /// Devices opened in a Device View recently, most-recent first, capped at
    /// <see cref="RecentlyViewedDeviceCount"/>. Shown on the Devices tab (if
    /// <see cref="ShowRecentlyViewedDevices"/> is on) and available as its own
    /// Dashboard widget.
    /// </summary>
    public List<RecentlyViewedDevice> RecentlyViewedDevices { get; set; } = new();

    /// <summary>Shows the recently-viewed strip above the Devices tab's grid. Does not affect the Dashboard widget, which is opt-in by adding it.</summary>
    public bool ShowRecentlyViewedDevices { get; set; } = true;

    /// <summary>How many devices <see cref="RecentlyViewedDevices"/> remembers - the same number is shown everywhere it appears.</summary>
    public int RecentlyViewedDeviceCount { get; set; } = 10;

    /// <summary>
    /// Devices pinned to the top of the Devices tab's grid (see
    /// DeviceItemViewModel.IsPinned) and available as their own Dashboard
    /// widget. Pinning only changes sort order - a pinned device still
    /// disappears under the Devices tab's own filters like any other row.
    /// </summary>
    public List<PinnedDevice> PinnedDevices { get; set; } = new();

    /// <summary>
    /// The accent colour used for buttons, selection highlights and links
    /// throughout the app, as "#RRGGBB". Deliberately separate from the fixed
    /// Critical/Warning/Ok severity colours, which never change.
    /// </summary>
    public string AccentColor { get; set; } = "#3B82F6";

    /// <summary>The base colour palette - see <see cref="AppTheme"/>.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>
    /// Show the connected server's own logo/favicon in the shell header when
    /// it has one. Off shows DashyNMS's own icon instead - some servers'
    /// branding does not suit every taste, or a shared/demo instance's mark
    /// is not what someone wants to see every time they open the app.
    /// </summary>
    public bool ShowServerLogo { get; set; } = true;

    /// <summary>
    /// Which map the Maps tab opens on: "Network", "Geographical", or (once
    /// custom maps exist) "custom:{id}". A string rather than an enum so a
    /// specific custom map can be named - see <see cref="DefaultMapNetwork"/>.
    /// </summary>
    public string DefaultMap { get; set; } = DefaultMapNetwork;

    public const string DefaultMapNetwork = "Network";

    public const string DefaultMapGeographical = "Geographical";

    /// <summary>Followed by a custom map's id - see Core.CustomMaps.</summary>
    public const string DefaultMapCustomPrefix = "custom:";

    /// <summary>
    /// The Geographical map's tile server - null for OpenStreetMap's standard
    /// tiles (LibreNMS's own default). Takes a full {z}/{x}/{y} template or
    /// LibreNMS's host-only leaflet.tile_url form - see Topology.TileUrlTemplate.
    /// </summary>
    public string? MapTileUrl { get; set; }

    public WindowPlacement? Window { get; set; }

    /// <summary>Remembered column widths/order and sort per DataGrid, keyed by a stable per-grid name (e.g. "Devices", "DeviceDetail.Ports") - see DataGridLayoutHelper.</summary>
    public Dictionary<string, GridLayout> GridLayouts { get; set; } = new();

    public AppSettings Clone() => new()
    {
        ServerUrl = ServerUrl,
        AllowUntrustedCertificate = AllowUntrustedCertificate,
        TimeoutSeconds = TimeoutSeconds,
        PollIntervalSeconds = PollIntervalSeconds,
        RememberToken = RememberToken,
        ServerTimestampsAreUtc = ServerTimestampsAreUtc,
        IncludeRecoveredAlerts = IncludeRecoveredAlerts,
        DeviceNameStyle = DeviceNameStyle,
        LoadAlertFaults = LoadAlertFaults,
        MinimiseToTrayOnClose = MinimiseToTrayOnClose,
        StartMinimised = StartMinimised,
        StartWithWindows = StartWithWindows,
        StartupTab = StartupTab,
        LastNotifiedUpdateVersion = LastNotifiedUpdateVersion,
        IncludePreviewBuilds = IncludePreviewBuilds,
        SuppressBulkAlertActionConfirmation = SuppressBulkAlertActionConfirmation,
        Notifications = Notifications.Clone(),
        Filter = Filter.Clone(),
        Unimus = Unimus.Clone(),
        Graylog = Graylog.Clone(),
        DbmThresholds = DbmThresholds.Clone(),
        SignalThresholds = SignalThresholds.Clone(),
        TemperatureThresholds = TemperatureThresholds.Clone(),
        FanSpeedThresholds = FanSpeedThresholds.Clone(),
        OverrideSensorLimitsWithAppThresholds = OverrideSensorLimitsWithAppThresholds,
        DashboardWidgets = DashboardWidgets.Select(w => w.Clone()).ToList(),
        RecentlyViewedDevices = RecentlyViewedDevices.Select(d => d.Clone()).ToList(),
        ShowRecentlyViewedDevices = ShowRecentlyViewedDevices,
        RecentlyViewedDeviceCount = RecentlyViewedDeviceCount,
        PinnedDevices = PinnedDevices.Select(d => d.Clone()).ToList(),
        AccentColor = AccentColor,
        Theme = Theme,
        ShowServerLogo = ShowServerLogo,
        DefaultMap = DefaultMap,
        MapTileUrl = MapTileUrl,
        Window = Window?.Clone(),
        GridLayouts = GridLayouts.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };

    /// <summary>Clamps anything a hand-edited settings file could have made nonsensical.</summary>
    public void Normalise()
    {
        if (TimeoutSeconds < 5) TimeoutSeconds = 5;
        if (TimeoutSeconds > 300) TimeoutSeconds = 300;
        if (PollIntervalSeconds < 15) PollIntervalSeconds = 15;
        if (PollIntervalSeconds > 3600) PollIntervalSeconds = 3600;

        Notifications ??= new NotificationSettings();
        Filter ??= new AlertFilterSettings();

        if (string.IsNullOrWhiteSpace(DefaultMap))
        {
            DefaultMap = DefaultMapNetwork;
        }

        if (string.IsNullOrWhiteSpace(MapTileUrl))
        {
            MapTileUrl = null;
        }
        DashboardWidgets ??= new List<DashboardWidget>();

        if (RecentlyViewedDeviceCount < 1) RecentlyViewedDeviceCount = 1;
        if (RecentlyViewedDeviceCount > MaxRecentlyViewedDeviceCount) RecentlyViewedDeviceCount = MaxRecentlyViewedDeviceCount;

        RecentlyViewedDevices ??= new List<RecentlyViewedDevice>();
        if (RecentlyViewedDevices.Count > RecentlyViewedDeviceCount)
        {
            RecentlyViewedDevices = RecentlyViewedDevices.Take(RecentlyViewedDeviceCount).ToList();
        }
        PinnedDevices ??= new List<PinnedDevice>();
        GridLayouts ??= new Dictionary<string, GridLayout>();
        DbmThresholds ??= new DbmThresholdSettings();
        SignalThresholds ??= new SignalThresholdSettings();
        TemperatureThresholds ??= BandThresholdSettings.TemperatureDefaults();
        FanSpeedThresholds ??= BandThresholdSettings.FanSpeedDefaults();
        Notifications.Normalise();
        DbmThresholds.Normalise();
        SignalThresholds.Normalise();
        TemperatureThresholds.Normalise(BandThresholdSettings.TemperatureDefaults());
        FanSpeedThresholds.Normalise(BandThresholdSettings.FanSpeedDefaults());

        foreach (var widget in DashboardWidgets)
        {
            widget.Normalise();
        }
    }
}

/// <summary>
/// Classifies a sensor reading into a severity. Implemented by each Health
/// tab category's threshold settings, so the sensor list can evaluate a row
/// without knowing which shape of thresholds it is governed by.
/// </summary>
public interface IThresholdEvaluator
{
    AlertSeverity Evaluate(double value);
}

/// <summary>
/// The dBm bands used to colour sensors on the Health tab. Optical power
/// readings run negative, so weaker signal means more negative: warning and
/// critical sit below the healthy range, not above it.
/// </summary>
public sealed class DbmThresholdSettings : IThresholdEvaluator
{
    /// <summary>At or below this (and above <see cref="CriticalThreshold"/>) is a warning.</summary>
    public double WarningThreshold { get; set; } = -12.5;

    /// <summary>At or below this is critical (signal too weak).</summary>
    public double CriticalThreshold { get; set; } = -14;

    /// <summary>
    /// At or above this, the reading is treated as "no data" (an unplugged
    /// port reporting a nonsensical high value) and is not flagged at all.
    /// </summary>
    public double IgnoreAtOrAbove { get; set; } = 39;

    /// <summary>
    /// At or below this, the reading is also treated as "no data": some
    /// hardware reports a fixed low sentinel (e.g. -39 dBm) for an unplugged
    /// or down port instead of a genuinely weak signal.
    /// </summary>
    public double IgnoreAtOrBelow { get; set; } = -39;

    public DbmThresholdSettings Clone() => new()
    {
        WarningThreshold = WarningThreshold,
        CriticalThreshold = CriticalThreshold,
        IgnoreAtOrAbove = IgnoreAtOrAbove,
        IgnoreAtOrBelow = IgnoreAtOrBelow,
    };

    /// <summary>Recovers a sane ordering if a hand-edited settings file breaks it.</summary>
    public void Normalise()
    {
        if (CriticalThreshold > WarningThreshold || WarningThreshold >= IgnoreAtOrAbove || IgnoreAtOrBelow >= CriticalThreshold)
        {
            WarningThreshold = -12.5;
            CriticalThreshold = -14;
            IgnoreAtOrAbove = 39;
            IgnoreAtOrBelow = -39;
        }
    }

    /// <summary>Classifies a dBm reading using these bands.</summary>
    public AlertSeverity Evaluate(double value)
    {
        if (value >= IgnoreAtOrAbove || value <= IgnoreAtOrBelow)
        {
            return AlertSeverity.Unknown;
        }

        if (value <= CriticalThreshold)
        {
            return AlertSeverity.Critical;
        }

        if (value <= WarningThreshold)
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }

    /// <summary>This category's low-side bounds, in the common shape <see cref="HybridThresholdEvaluator"/> merges against a sensor's own limits. There is no high side here - see the class remarks.</summary>
    public ThresholdBounds ToBounds() => new(LowCritical: CriticalThreshold, LowWarning: WarningThreshold, HighWarning: null, HighCritical: null);
}

/// <summary>
/// The bands used to colour LibreNMS's generic "signal" class sensors on the
/// Health tab. Same shape as <see cref="DbmThresholdSettings"/> (a weak
/// signal is bad, and hardware can report a sentinel value at either extreme
/// for "no signal"), kept as its own settings type since the two are
/// configured, displayed and persisted independently.
/// </summary>
public sealed class SignalThresholdSettings : IThresholdEvaluator
{
    /// <summary>At or below this (and above <see cref="CriticalThreshold"/>) is a warning.</summary>
    public double WarningThreshold { get; set; } = -70;

    /// <summary>At or below this is critical (signal too weak).</summary>
    public double CriticalThreshold { get; set; } = -80;

    /// <summary>At or above this, the reading is treated as "no data" rather than flagged.</summary>
    public double IgnoreAtOrAbove { get; set; } = 0;

    /// <summary>At or below this, the reading is also treated as "no data".</summary>
    public double IgnoreAtOrBelow { get; set; } = -100;

    public SignalThresholdSettings Clone() => new()
    {
        WarningThreshold = WarningThreshold,
        CriticalThreshold = CriticalThreshold,
        IgnoreAtOrAbove = IgnoreAtOrAbove,
        IgnoreAtOrBelow = IgnoreAtOrBelow,
    };

    /// <summary>Recovers a sane ordering if a hand-edited settings file breaks it.</summary>
    public void Normalise()
    {
        if (CriticalThreshold > WarningThreshold || WarningThreshold >= IgnoreAtOrAbove || IgnoreAtOrBelow >= CriticalThreshold)
        {
            WarningThreshold = -70;
            CriticalThreshold = -80;
            IgnoreAtOrAbove = 0;
            IgnoreAtOrBelow = -100;
        }
    }

    public AlertSeverity Evaluate(double value)
    {
        if (value >= IgnoreAtOrAbove || value <= IgnoreAtOrBelow)
        {
            return AlertSeverity.Unknown;
        }

        if (value <= CriticalThreshold)
        {
            return AlertSeverity.Critical;
        }

        if (value <= WarningThreshold)
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }

    /// <summary>This category's low-side bounds - see <see cref="DbmThresholdSettings.ToBounds"/>.</summary>
    public ThresholdBounds ToBounds() => new(LowCritical: CriticalThreshold, LowWarning: WarningThreshold, HighWarning: null, HighCritical: null);
}

/// <summary>
/// A symmetric band used where both a low and a high extreme are a fault -
/// temperature and fan speed on the Health tab. Unlike
/// <see cref="DbmThresholdSettings"/>, there is no "no data" sentinel: the
/// healthy range simply sits between the two warning thresholds.
/// </summary>
public sealed class BandThresholdSettings : IThresholdEvaluator
{
    /// <summary>At or below this is critical (too low).</summary>
    public double LowCritical { get; set; }

    /// <summary>At or below this (and above <see cref="LowCritical"/>) is a warning.</summary>
    public double LowWarning { get; set; }

    /// <summary>At or above this (and below <see cref="HighCritical"/>) is a warning.</summary>
    public double HighWarning { get; set; }

    /// <summary>At or above this is critical (too high).</summary>
    public double HighCritical { get; set; }

    /// <summary>
    /// Starting-point thresholds for temperature (degrees Celsius). Network
    /// hardware varies a lot in what counts as hot, so these are a generic
    /// guideline meant to be tuned in Settings, not a precise default.
    /// </summary>
    public static BandThresholdSettings TemperatureDefaults() => new()
    {
        LowCritical = -10,
        LowWarning = 0,
        HighWarning = 60,
        HighCritical = 75,
    };

    /// <summary>
    /// Starting-point thresholds for fan speed (RPM). Absolute RPM varies far
    /// more by fan size than temperature does by device, so these defaults
    /// are even more of a placeholder - tune them per fleet in Settings.
    /// </summary>
    public static BandThresholdSettings FanSpeedDefaults() => new()
    {
        LowCritical = 500,
        LowWarning = 1000,
        HighWarning = 20000,
        HighCritical = 25000,
    };

    public BandThresholdSettings Clone() => new()
    {
        LowCritical = LowCritical,
        LowWarning = LowWarning,
        HighWarning = HighWarning,
        HighCritical = HighCritical,
    };

    /// <summary>Recovers a sane ordering if a hand-edited settings file breaks it.</summary>
    public void Normalise(BandThresholdSettings defaults)
    {
        if (!(LowCritical <= LowWarning && LowWarning <= HighWarning && HighWarning <= HighCritical))
        {
            LowCritical = defaults.LowCritical;
            LowWarning = defaults.LowWarning;
            HighWarning = defaults.HighWarning;
            HighCritical = defaults.HighCritical;
        }
    }

    public AlertSeverity Evaluate(double value)
    {
        if (value <= LowCritical || value >= HighCritical)
        {
            return AlertSeverity.Critical;
        }

        if (value <= LowWarning || value >= HighWarning)
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }

    /// <summary>This category's bounds - see <see cref="DbmThresholdSettings.ToBounds"/>. Unlike dBm/signal, both sides are real bounds here, not just a low side.</summary>
    public ThresholdBounds ToBounds() => new(LowCritical, LowWarning, HighWarning, HighCritical);
}

/// <summary>
/// Four optional boundaries - at-or-below/at-or-above is a fault - shared by
/// every Health tab category's settings (see each type's <c>ToBounds()</c>)
/// and by a sensor's own LibreNMS-configured limits, so
/// <see cref="HybridThresholdEvaluator"/> can merge the two without caring
/// which shape of settings produced either side.
/// </summary>
public readonly record struct ThresholdBounds(double? LowCritical, double? LowWarning, double? HighWarning, double? HighCritical);

/// <summary>
/// Classifies a reading against whichever of a sensor's own LibreNMS-configured
/// limits (sensor_limit/_warn/_low/_low_warn) are actually set, falling back to
/// the app's own configured threshold for any bound the sensor leaves
/// unconfigured - or, if <see cref="AppSettings.OverrideSensorLimitsWithAppThresholds"/>
/// is on, always uses the app's bound regardless of what the sensor specifies.
/// A device-specific limit is normally the more accurate one: LibreNMS's app
/// threshold is one fleet-wide guess, while a sensor's own limit (when the
/// device or its discovery module set one) reflects that specific hardware.
/// </summary>
public sealed class HybridThresholdEvaluator : IThresholdEvaluator
{
    private readonly double? _lowCritical;
    private readonly double? _lowWarning;
    private readonly double? _highWarning;
    private readonly double? _highCritical;
    private readonly double? _ignoreAtOrAbove;
    private readonly double? _ignoreAtOrBelow;

    public HybridThresholdEvaluator(
        ThresholdBounds appBounds,
        ThresholdBounds sensorBounds,
        bool overrideSensorLimitsWithAppThresholds,
        double? ignoreAtOrAbove = null,
        double? ignoreAtOrBelow = null)
    {
        double? Merge(double? sensorValue, double? appValue) =>
            !overrideSensorLimitsWithAppThresholds && sensorValue.HasValue ? sensorValue : appValue;

        _lowCritical = Merge(sensorBounds.LowCritical, appBounds.LowCritical);
        _lowWarning = Merge(sensorBounds.LowWarning, appBounds.LowWarning);
        _highWarning = Merge(sensorBounds.HighWarning, appBounds.HighWarning);
        _highCritical = Merge(sensorBounds.HighCritical, appBounds.HighCritical);

        // The "this reading means the port is unplugged/down" sentinel bands
        // (dBm/signal only) are a DashyNMS concept with no equivalent on the
        // sensor itself, so these always come from the app's own settings.
        _ignoreAtOrAbove = ignoreAtOrAbove;
        _ignoreAtOrBelow = ignoreAtOrBelow;
    }

    public AlertSeverity Evaluate(double value)
    {
        if (_ignoreAtOrAbove is { } above && value >= above)
        {
            return AlertSeverity.Unknown;
        }

        if (_ignoreAtOrBelow is { } below && value <= below)
        {
            return AlertSeverity.Unknown;
        }

        if (_lowCritical is { } lowCritical && value <= lowCritical)
        {
            return AlertSeverity.Critical;
        }

        if (_highCritical is { } highCritical && value >= highCritical)
        {
            return AlertSeverity.Critical;
        }

        if (_lowWarning is { } lowWarning && value <= lowWarning)
        {
            return AlertSeverity.Warning;
        }

        if (_highWarning is { } highWarning && value >= highWarning)
        {
            return AlertSeverity.Warning;
        }

        return AlertSeverity.Ok;
    }
}

/// <summary>
/// One entry in the Devices tab's "recently viewed" strip. DisplayName is a
/// snapshot taken at the moment the device was opened - like
/// <see cref="PinnedSensor"/>, a cached label so the strip has something to
/// show even before the device list itself has loaded this session, not a
/// live-updating name.
/// </summary>
public sealed class RecentlyViewedDevice
{
    public int DeviceId { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset ViewedAt { get; set; }

    public RecentlyViewedDevice Clone() => new()
    {
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        ViewedAt = ViewedAt,
    };
}

/// <summary>
/// A device pinned to the top of the Devices tab (see <see cref="AppSettings.PinnedDevices"/>).
/// DisplayName is a snapshot taken at the moment it was pinned - same
/// reasoning as <see cref="RecentlyViewedDevice"/> and <see cref="PinnedSensor"/>.
/// </summary>
public sealed class PinnedDevice
{
    public int DeviceId { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset PinnedAt { get; set; }

    public PinnedDevice Clone() => new()
    {
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        PinnedAt = PinnedAt,
    };
}

/// <summary>
/// A sensor added to a Sensors dashboard widget. Identified by <see cref="SensorId"/>
/// (stable and unique across the whole LibreNMS instance); the rest is a
/// cached label so the widget has something to show even before the next
/// live fetch confirms the sensor still exists.
/// </summary>
public sealed class PinnedSensor
{
    public int SensorId { get; set; }

    public int DeviceId { get; set; }

    /// <summary>e.g. "dbm", "signal", "temperature", "fanspeed" - decides which thresholds apply.</summary>
    public string SensorClass { get; set; } = string.Empty;

    public string? DeviceName { get; set; }

    public string? Description { get; set; }

    public PinnedSensor Clone() => new()
    {
        SensorId = SensorId,
        DeviceId = DeviceId,
        SensorClass = SensorClass,
        DeviceName = DeviceName,
        Description = Description,
    };
}

/// <summary>
/// A widget placed on the Dashboard tab's free-form canvas: its type, title,
/// and where the user dragged/resized it to. Identified by <see cref="Id"/>
/// so it survives being moved or renamed.
/// </summary>
public sealed class DashboardWidget
{
    /// <summary>
    /// Pixels per grid cell (both axes - cells are square). Fixed rather than
    /// computed from the viewport, so a widget's Column/Row/span are already
    /// resolution-independent: the same layout just reveals more or fewer
    /// cells on a bigger or smaller display, with no rescaling needed.
    /// </summary>
    public const double CellSize = 40;

    public const int DefaultColumnSpan = 10;
    public const int DefaultRowSpan = 7;
    public const int MinColumnSpan = 6;
    public const int MinRowSpan = 4;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>e.g. "Sensors" - which kind of widget content to render.</summary>
    public string WidgetType { get; set; } = "Sensors";

    public string Title { get; set; } = "Widget";

    /// <summary>0-based grid column the widget's left edge sits at.</summary>
    public int Column { get; set; }

    /// <summary>0-based grid row the widget's top edge sits at.</summary>
    public int Row { get; set; }

    public int ColumnSpan { get; set; } = DefaultColumnSpan;

    public int RowSpan { get; set; } = DefaultRowSpan;

    /// <summary>For a "Sensors" widget: which sensors it shows. Unused by other widget types.</summary>
    public List<PinnedSensor> Sensors { get; set; } = new();

    /// <summary>For an "Alerts" widget: which severities to show. Unused by other widget types.</summary>
    public bool AlertsShowCritical { get; set; } = true;

    public bool AlertsShowWarning { get; set; } = true;

    /// <summary>For an "Alerts" widget: whether acknowledged alerts count towards the two severities above.</summary>
    public bool AlertsIncludeAcknowledged { get; set; }

    /// <summary>For a "Graph" widget: which device's graph it shows. Unused by other widget types.</summary>
    public int? GraphDeviceId { get; set; }

    /// <summary>For a "Graph" widget: the graph name (from that device's /graphs or /health listing), null until chosen.</summary>
    public string? GraphName { get; set; }

    public GraphTimeRangePreset GraphTimeRangePreset { get; set; } = GraphTimeRangePreset.Day;

    /// <summary>For a "Graph" widget with a Custom time range.</summary>
    public DateTime? GraphCustomFrom { get; set; }

    public DateTime? GraphCustomTo { get; set; }

    public DashboardWidget Clone() => new()
    {
        Id = Id,
        WidgetType = WidgetType,
        Title = Title,
        Column = Column,
        Row = Row,
        ColumnSpan = ColumnSpan,
        RowSpan = RowSpan,
        Sensors = Sensors.Select(s => s.Clone()).ToList(),
        AlertsShowCritical = AlertsShowCritical,
        AlertsShowWarning = AlertsShowWarning,
        AlertsIncludeAcknowledged = AlertsIncludeAcknowledged,
        GraphDeviceId = GraphDeviceId,
        GraphName = GraphName,
        GraphTimeRangePreset = GraphTimeRangePreset,
        GraphCustomFrom = GraphCustomFrom,
        GraphCustomTo = GraphCustomTo,
    };

    /// <summary>Clamps anything a hand-edited settings file could have made nonsensical.</summary>
    public void Normalise()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Title)) Title = "Widget";
        if (Column < 0) Column = 0;
        if (Row < 0) Row = 0;
        if (ColumnSpan < MinColumnSpan) ColumnSpan = MinColumnSpan;
        if (RowSpan < MinRowSpan) RowSpan = MinRowSpan;
        Sensors ??= new List<PinnedSensor>();
    }
}

/// <summary>
/// Maps a sensor's LibreNMS class to the Warning/Critical thresholds and unit
/// that apply to it. The single place both the Health tab and the Dashboard's
/// pinned-sensor widget go to classify a reading, so adding a sensor class to
/// one automatically covers the other.
/// </summary>
public static class SensorCategoryRegistry
{
    /// <summary>
    /// <see cref="Thresholds"/> takes the sensor being classified, not just
    /// the app's settings: the resulting evaluator is a <see cref="HybridThresholdEvaluator"/>
    /// that prefers that sensor's own configured limit for whichever bound it
    /// specifies, over the app's fleet-wide one.
    /// </summary>
    public sealed record Entry(Func<AppSettings, Sensor, IThresholdEvaluator> Thresholds, string UnitSuffix, string DisplayName);

    private static ThresholdBounds SensorBounds(Sensor sensor) => new(sensor.LimitLow, sensor.LimitLowWarn, sensor.LimitHighWarn, sensor.LimitHigh);

    private static readonly Dictionary<string, Entry> ByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dbm"] = new Entry(
            (settings, sensor) => new HybridThresholdEvaluator(
                settings.DbmThresholds.ToBounds(), SensorBounds(sensor), settings.OverrideSensorLimitsWithAppThresholds,
                settings.DbmThresholds.IgnoreAtOrAbove, settings.DbmThresholds.IgnoreAtOrBelow),
            " dBm", "dBm"),
        ["signal"] = new Entry(
            (settings, sensor) => new HybridThresholdEvaluator(
                settings.SignalThresholds.ToBounds(), SensorBounds(sensor), settings.OverrideSensorLimitsWithAppThresholds,
                settings.SignalThresholds.IgnoreAtOrAbove, settings.SignalThresholds.IgnoreAtOrBelow),
            string.Empty, "Signal"),
        ["temperature"] = new Entry(
            (settings, sensor) => new HybridThresholdEvaluator(
                settings.TemperatureThresholds.ToBounds(), SensorBounds(sensor), settings.OverrideSensorLimitsWithAppThresholds),
            " °C", "Temperature"),
        ["fanspeed"] = new Entry(
            (settings, sensor) => new HybridThresholdEvaluator(
                settings.FanSpeedThresholds.ToBounds(), SensorBounds(sensor), settings.OverrideSensorLimitsWithAppThresholds),
            " RPM", "Fan speed"),
    };

    /// <summary>All sensor classes the app understands, in display order.</summary>
    public static IReadOnlyList<string> KnownClasses { get; } = new[] { "dbm", "signal", "temperature", "fanspeed" };

    public static Entry? Resolve(string? sensorClass)
        => sensorClass is not null && ByClass.TryGetValue(sensorClass, out var entry) ? entry : null;
}

/// <summary>How long a Windows toast stays on screen.</summary>
public enum ToastPersistence
{
    /// <summary>Standard toast: shows for a few seconds, then moves to the notification centre.</summary>
    Transient = 0,

    /// <summary>Stays on screen until the user dismisses or acts on it (toast scenario "Reminder").</summary>
    UntilDismissed = 1,

    /// <summary>Stays on screen and loops an alarm sound (toast scenario "Alarm").</summary>
    UntilDismissedWithAlarm = 2,
}

/// <summary>Per-severity notification behaviour.</summary>
public sealed class SeverityNotificationSettings
{
    public bool Enabled { get; set; } = true;

    public ToastPersistence Persistence { get; set; } = ToastPersistence.Transient;

    public bool PlaySound { get; set; } = true;

    public SeverityNotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Persistence = Persistence,
        PlaySound = PlaySound,
    };
}

/// <summary>Notification settings, including the per-severity "stickiness".</summary>
public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Critical alerts stay on screen by default.</summary>
    public SeverityNotificationSettings Critical { get; set; } = new()
    {
        Enabled = true,
        Persistence = ToastPersistence.UntilDismissed,
        PlaySound = true,
    };

    /// <summary>Warnings are transient by default.</summary>
    public SeverityNotificationSettings Warning { get; set; } = new()
    {
        Enabled = true,
        Persistence = ToastPersistence.Transient,
        PlaySound = true,
    };

    /// <summary>Alerts from rules with severity "ok", which are rare.</summary>
    public SeverityNotificationSettings Ok { get; set; } = new()
    {
        Enabled = false,
        Persistence = ToastPersistence.Transient,
        PlaySound = false,
    };

    /// <summary>Toast when an alert clears.</summary>
    public bool NotifyOnRecovery { get; set; } = true;

    /// <summary>Toast when an alert is acknowledged (usually by someone else).</summary>
    public bool NotifyOnAcknowledge { get; set; }

    /// <summary>
    /// Do not toast for everything already outstanding when the app starts.
    /// Only alerts that appear after the first successful poll raise a toast.
    /// </summary>
    public bool SuppressOnFirstPoll { get; set; } = true;

    /// <summary>
    /// Above this many new alerts in one poll, collapse them into a single
    /// summary toast rather than flooding the notification centre.
    /// </summary>
    public int MaxToastsPerPoll { get; set; } = 5;

    /// <summary>Suppress toasts between <see cref="QuietHoursStartHour"/> and <see cref="QuietHoursEndHour"/>.</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Local hour (0-23) at which quiet hours begin.</summary>
    public int QuietHoursStartHour { get; set; } = 22;

    /// <summary>Local hour (0-23) at which quiet hours end.</summary>
    public int QuietHoursEndHour { get; set; } = 7;

    /// <summary>Critical alerts still toast during quiet hours.</summary>
    public bool QuietHoursAllowCritical { get; set; } = true;

    public SeverityNotificationSettings ForSeverity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => Critical,
        AlertSeverity.Warning => Warning,
        _ => Ok,
    };

    /// <summary>True if a toast should be suppressed right now because of quiet hours.</summary>
    public bool IsInQuietHours(DateTime localNow, AlertSeverity severity)
    {
        if (!QuietHoursEnabled)
        {
            return false;
        }

        if (QuietHoursAllowCritical && severity == AlertSeverity.Critical)
        {
            return false;
        }

        var hour = localNow.Hour;

        // A window that wraps past midnight, e.g. 22:00 -> 07:00.
        return QuietHoursStartHour <= QuietHoursEndHour
            ? hour >= QuietHoursStartHour && hour < QuietHoursEndHour
            : hour >= QuietHoursStartHour || hour < QuietHoursEndHour;
    }

    public void Normalise()
    {
        Critical ??= new SeverityNotificationSettings();
        Warning ??= new SeverityNotificationSettings();
        Ok ??= new SeverityNotificationSettings();

        if (MaxToastsPerPoll < 1) MaxToastsPerPoll = 1;
        if (MaxToastsPerPoll > 25) MaxToastsPerPoll = 25;
        if (QuietHoursStartHour is < 0 or > 23) QuietHoursStartHour = 22;
        if (QuietHoursEndHour is < 0 or > 23) QuietHoursEndHour = 7;
    }

    public NotificationSettings Clone() => new()
    {
        Enabled = Enabled,
        Critical = Critical.Clone(),
        Warning = Warning.Clone(),
        Ok = Ok.Clone(),
        NotifyOnRecovery = NotifyOnRecovery,
        NotifyOnAcknowledge = NotifyOnAcknowledge,
        SuppressOnFirstPoll = SuppressOnFirstPoll,
        MaxToastsPerPoll = MaxToastsPerPoll,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStartHour = QuietHoursStartHour,
        QuietHoursEndHour = QuietHoursEndHour,
        QuietHoursAllowCritical = QuietHoursAllowCritical,
    };
}

/// <summary>The filter chips the user last had selected.</summary>
/// <summary>
/// Configuration for the Unimus config-backup integration (issue #115) - a
/// second, independent API this app talks to directly (its own URL, its own
/// token, its own SSL trust setting) rather than through LibreNMS, even
/// though LibreNMS ships its own server-side Unimus client
/// (<c>app/ApiClients/Unimus.php</c>) - that integration lives entirely in
/// LibreNMS's web UI and isn't reachable via its versioned API.
/// </summary>
public sealed class UnimusSettings
{
    public bool Enabled { get; set; }

    /// <summary>Root URL of the Unimus instance, e.g. https://unimus.example.com/.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Accept self-signed or internally-issued certificates - deliberately
    /// separate from the LibreNMS connection's own
    /// <see cref="AppSettings.AllowUntrustedCertificate"/>, since Unimus is
    /// commonly run as an internal-only tool with its own, unrelated
    /// certificate trust story.
    /// </summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>
    /// LibreNMS's own discovery domain suffix (its <c>mydomain</c> config) -
    /// one of the candidates tried when matching a LibreNMS device to a
    /// Unimus one by hostname. Not exposed via LibreNMS's API, so this has
    /// to be entered here too if it's set on the LibreNMS side.
    /// </summary>
    public string? MyDomain { get; set; }

    public UnimusSettings Clone() => new()
    {
        Enabled = Enabled,
        Url = Url,
        AllowUntrustedCertificate = AllowUntrustedCertificate,
        MyDomain = MyDomain,
    };
}

/// <summary>
/// Configuration for the Graylog log integration (issue #114) - mirrors
/// LibreNMS's own Graylog settings (Settings, External, Graylog; the
/// <c>graylog.*</c> keys in its <c>config_definitions.json</c>) field for
/// field, since LibreNMS's API doesn't expose its config for this app to
/// read. Like Unimus, this app talks to Graylog directly with its own
/// credentials rather than through LibreNMS, whose Graylog pages only exist
/// in its web UI.
/// </summary>
public sealed class GraylogSettings
{
    /// <summary>LibreNMS's <c>graylog.version</c> values.</summary>
    public const string Version20 = "2.0";

    public const string Version21 = "2.1";

    public const string VersionOther = "other";

    /// <summary>LibreNMS's <c>graylog.device-page.loglevel</c> default - every level.</summary>
    public const int DefaultLogLevel = 7;

    /// <summary>LibreNMS's <c>graylog.device-page.rowCount</c> default.</summary>
    public const int DefaultRowCount = 10;

    /// <summary>LibreNMS's <c>graylog.query.field</c> default.</summary>
    public const string DefaultQueryField = "source";

    /// <summary>The most messages a page can ask Graylog for - LibreNMS's own largest page size is 250; this leaves room without asking for a huge page.</summary>
    public const int MaxRowCount = 500;

    public bool Enabled { get; set; }

    /// <summary><c>graylog.server</c> - the Graylog server's address, e.g. https://graylog.example.com.</summary>
    public string? Server { get; set; }

    /// <summary><c>graylog.port</c> - optional; blank means the scheme's default (80/443).</summary>
    public int? Port { get; set; }

    /// <summary><c>graylog.version</c> - "2.1" (2.1 or newer, the API lives under /api), "2.0" (older, no /api prefix) or "other" (use <see cref="BaseUri"/>).</summary>
    public string Version { get; set; } = Version21;

    /// <summary><c>graylog.base_uri</c> - the search path to use instead of the default, only when <see cref="Version"/> is "other".</summary>
    public string? BaseUri { get; set; }

    /// <summary><c>graylog.username</c>. For a Graylog access token, this is the token and the password is the word "token".</summary>
    public string? Username { get; set; }

    /// <summary>Accept a self-signed or internally-issued certificate - not a LibreNMS setting (LibreNMS uses its own server-wide HTTP client options), but the same per-integration choice Unimus has here.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary><c>graylog.timezone</c> - show message times in this zone rather than this PC's own. Takes a Windows or IANA name (e.g. "Europe/London", as LibreNMS itself would); blank means local time.</summary>
    public string? Timezone { get; set; }

    /// <summary><c>graylog.device-page.loglevel</c> - the highest syslog level (0-7) shown by default on a device's Graylog tab.</summary>
    public int DeviceLogLevel { get; set; } = DefaultLogLevel;

    /// <summary><c>graylog.device-page.rowCount</c> - how many messages a device's Graylog tab shows per page by default.</summary>
    public int DeviceRowCount { get; set; } = DefaultRowCount;

    /// <summary><c>graylog.query.field</c> - the Graylog message field matched against a device's addresses.</summary>
    public string QueryField { get; set; } = DefaultQueryField;

    /// <summary><c>graylog.match-any-address</c> - match every IP address on the device, not just its primary addresses and names.</summary>
    public bool MatchAnyAddress { get; set; }

    /// <summary>Whether the Logs tab's Graylog view re-fetches on its own - its Auto-update toggle, remembered between sessions. Not a LibreNMS setting.</summary>
    public bool LogsAutoUpdate { get; set; } = true;

    /// <summary>How often the Logs tab auto-updates, in seconds.</summary>
    public int LogsAutoUpdateSeconds { get; set; } = DefaultLogsAutoUpdateSeconds;

    public const int DefaultLogsAutoUpdateSeconds = 30;

    /// <summary>How many messages the Logs tab loads per page by default.</summary>
    public const int DefaultLogsRowCount = 50;

    public GraylogSettings Clone() => new()
    {
        Enabled = Enabled,
        Server = Server,
        Port = Port,
        Version = Version,
        BaseUri = BaseUri,
        Username = Username,
        AllowUntrustedCertificate = AllowUntrustedCertificate,
        Timezone = Timezone,
        DeviceLogLevel = DeviceLogLevel,
        DeviceRowCount = DeviceRowCount,
        QueryField = QueryField,
        MatchAnyAddress = MatchAnyAddress,
        LogsAutoUpdate = LogsAutoUpdate,
        LogsAutoUpdateSeconds = LogsAutoUpdateSeconds,
    };
}

public sealed class AlertFilterSettings
{
    public bool ShowCritical { get; set; } = true;

    public bool ShowWarning { get; set; } = true;

    public bool ShowUnknownSeverity { get; set; } = true;

    public bool ShowAcknowledged { get; set; } = true;

    public string? SearchText { get; set; }

    public AlertFilterSettings Clone() => new()
    {
        ShowCritical = ShowCritical,
        ShowWarning = ShowWarning,
        ShowUnknownSeverity = ShowUnknownSeverity,
        ShowAcknowledged = ShowAcknowledged,
        SearchText = SearchText,
    };
}

/// <summary>Remembered size and position of the main window.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximised { get; set; }

    public WindowPlacement Clone() => new()
    {
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        Maximised = Maximised,
    };
}

/// <summary>
/// Remembered column widths, visibility, order and sort for one DataGrid -
/// see DataGridLayoutHelper. Columns are matched by position (list index =
/// the column's original XAML declaration order) rather than a separately
/// invented string id; a saved layout with a different column count than
/// the live grid (e.g. after a future column-set change) is simply
/// ignored rather than mis-applied, since this is a remembered preference,
/// not data. <see cref="ColumnOrder"/> is separate from that positional
/// list precisely because DataGridColumn.DisplayIndex cannot safely be
/// reassigned one column at a time from code in declaration order (WPF
/// validates the whole column set as a permutation at every intermediate
/// assignment, and a naive restore loop can throw
/// ArgumentOutOfRangeException by passing through an invalid in-between
/// state - confirmed against a live crash, issue #15); see
/// DataGridLayoutHelper.Apply for the ascending-target-order assignment
/// that avoids it (issue #40).
/// </summary>
public sealed class GridLayout
{
    public List<GridColumnLayout> Columns { get; set; } = new();

    /// <summary>
    /// For each visual position 0..N-1, the declaration-index (into
    /// <see cref="Columns"/>/the live grid's own Columns collection) of the
    /// column that sits there - null means "leave the live order alone",
    /// so an older saved layout with no order at all is unaffected.
    /// </summary>
    public List<int>? ColumnOrder { get; set; }

    public int? SortColumnIndex { get; set; }

    public bool SortDescending { get; set; }

    public GridLayout Clone() => new()
    {
        Columns = Columns.Select(c => c.Clone()).ToList(),
        ColumnOrder = ColumnOrder?.ToList(),
        SortColumnIndex = SortColumnIndex,
        SortDescending = SortDescending,
    };
}

/// <summary>One column's remembered width and visibility within a <see cref="GridLayout"/>.</summary>
public sealed class GridColumnLayout
{
    /// <summary>A pixel width, or - when <see cref="IsStarWidth"/> - the star factor (e.g. 1, 2) rather than a pixel count.</summary>
    public double Width { get; set; }

    /// <summary>True when this column was originally star-sized ("fill remaining space") - <see cref="Width"/> is then its star factor, not a pixel count, so it never gets pinned to a fixed size just because its layout was saved once.</summary>
    public bool IsStarWidth { get; set; }

    /// <summary>False when the user hid this column via a grid's "Columns" chooser (issue #40). Absent in an older saved layout, which defaults to true - a column no earlier version of the app could ever hide.</summary>
    public bool IsVisible { get; set; } = true;

    public GridColumnLayout Clone() => new() { Width = Width, IsStarWidth = IsStarWidth, IsVisible = IsVisible };
}
