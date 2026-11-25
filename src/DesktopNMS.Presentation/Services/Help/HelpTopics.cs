using System.Collections.Generic;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Services.Help;

/// <summary>A page of the guide, and the heading on it to open at.</summary>
public sealed record HelpTopic(string PageId, string? Anchor = null);

/// <summary>
/// Which page of the guide explains each part of the app (#604). A page's id
/// is its name on the wiki; a test holds every one of these to a page and a
/// heading the guide has.
/// </summary>
public static class HelpTopics
{
    public static HelpTopic Home { get; } = new("Home");

    /// <summary>First run, and the sign-in window.</summary>
    public static HelpTopic SignIn { get; } = new("Getting-started", "sign-in");

    public static HelpTopic For(MainTab tab) => tab switch
    {
        MainTab.Dashboard => new("Build-your-dashboard"),
        MainTab.Devices or MainTab.Groups or MainTab.Locations => new("Investigate-a-device"),
        MainTab.Neighbours => new("Investigate-a-device", "follow-the-connection-to-another-device"),
        MainTab.Health => new("Use-maps-and-health-readings", "check-readings-across-the-network"),
        MainTab.MapsNetwork or MainTab.MapsCustom => new("Use-maps-and-health-readings"),
        MainTab.MapsGeographical => new("Use-maps-and-health-readings", "use-the-geographical-map"),
        MainTab.Alerts => new("Investigate-and-acknowledge-alerts"),
        MainTab.Rules or MainTab.Templates => new("Manage-alert-rules"),
        MainTab.LogsGraylog => new("Search-and-export-logs", "search-graylog-messages"),
        MainTab.LogsEvents or MainTab.LogsAlerts => new("Search-and-export-logs"),
        MainTab.Intelligence => new("Ask-about-your-network-with-Intelligence"),
        MainTab.NetworkSearch => new("Find-anything-with-Network-Search"),
        _ => Home,
    };

    public static HelpTopic For(SettingsSection section) => section switch
    {
        SettingsSection.Server => new("Change-your-connection-and-desktop-settings", "change-the-server-address-or-token"),
        SettingsSection.Polling => new("Change-your-connection-and-desktop-settings", "change-refresh-timing"),
        SettingsSection.Appearance or SettingsSection.Window => new("Change-your-connection-and-desktop-settings", "choose-your-appearance-and-opening-tab"),
        SettingsSection.Updates => new("Change-your-connection-and-desktop-settings", "choose-an-update-channel"),
        SettingsSection.HealthThresholds => new("Use-maps-and-health-readings", "understand-warning-and-critical-colours"),
        SettingsSection.AlertDisplay => new("Investigate-and-acknowledge-alerts"),
        SettingsSection.Notifications => new("Choose-which-alerts-notify-you"),
        SettingsSection.Maps => new("Use-maps-and-health-readings"),
        SettingsSection.Graylog => new("Connect-Graylog-and-Unimus", "connect-graylog"),
        SettingsSection.Integrations => new("Connect-Graylog-and-Unimus", "connect-unimus"),
        SettingsSection.Intelligence => new("Ask-about-your-network-with-Intelligence"),
        SettingsSection.IntelligenceProviders => new("Ask-about-your-network-with-Intelligence", "set-up-a-provider"),
        SettingsSection.Diagnostics => new("Troubleshooting", "report-a-problem"),
        _ => new("Change-your-connection-and-desktop-settings"),
    };

    /// <summary>The page of this guide a wiki address names, or null for any other address - such as a page written since.</summary>
    public static HelpTopic? FromAddress(System.Uri address)
    {
        const string wiki = "/DashyNMS/desktop/wiki/";
        if (address.Scheme != System.Uri.UriSchemeHttps || !address.Host.Equals("github.com", System.StringComparison.OrdinalIgnoreCase)
            || !address.AbsolutePath.StartsWith(wiki, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var page = System.Uri.UnescapeDataString(address.AbsolutePath[wiki.Length..]).Trim('/');
        return page.Length == 0 || page.Contains('/') || !BundledHelp.HasPage(page) ? null
            : new HelpTopic(page, address.Fragment.Length > 1 ? System.Uri.UnescapeDataString(address.Fragment[1..]) : null);
    }

    /// <summary>Every topic there is, for the test that the guide has them all.</summary>
    internal static IEnumerable<HelpTopic> All
    {
        get
        {
            yield return Home;
            yield return SignIn;
            yield return new("Getting-started", "try-your-first-everyday-tasks");
            foreach (var tab in System.Enum.GetValues<MainTab>())
            {
                yield return For(tab);
            }

            foreach (var section in System.Enum.GetValues<SettingsSection>())
            {
                yield return For(section);
            }
        }
    }
}
