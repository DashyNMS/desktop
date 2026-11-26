namespace DesktopNMS.ViewModels;

/// <summary>Which section of the settings dialog is showing.</summary>
public enum SettingsSection
{
    Polling,
    AlertDisplay,
    HealthThresholds,
    Notifications,
    Devices,
    Maps,
    Window,
    Appearance,
    Server,
    Integrations,
    Graylog,
    Intelligence,
    About,
    SensorDefinitions,
    ServerInformation,
    Updates,
    Diagnostics,

    /// <summary>Intelligence's providers (#316): who answers, and how each connects.</summary>
    IntelligenceProviders,
}
