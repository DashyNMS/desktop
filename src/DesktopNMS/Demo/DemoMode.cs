using System.IO;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;

namespace DesktopNMS.Demo;

/// <summary>
/// Demo mode: the whole app against <see cref="DemoServer"/>'s example network
/// instead of a real LibreNMS - offered on the sign-in window, and what the
/// README and release screenshots are rendered from. It runs in a copy of the
/// app started with <see cref="Argument"/>, in its own data folder, so the
/// real settings, token and layouts are never read or touched; leaving it
/// (Sign out) starts the normal app again.
/// </summary>
public sealed class DemoMode
{
    /// <summary>Starts the app in demo mode.</summary>
    public const string Argument = "--demo";

    /// <summary>With <see cref="Argument"/>: render the screenshot tour into the folder that follows, then exit.</summary>
    public const string ScreenshotsArgument = "--screenshots";

    public DemoMode(bool isActive, string? screenshotFolder = null)
    {
        IsActive = isActive;
        ScreenshotFolder = screenshotFolder;
    }

    public bool IsActive { get; }

    /// <summary>Set when rendering screenshots rather than showing the demo to someone.</summary>
    public string? ScreenshotFolder { get; }

    public bool IsRenderingScreenshots => ScreenshotFolder is not null;

    /// <summary>Demo mode's own data folder - emptied each time the demo starts.</summary>
    public static string DataDirectory => Path.Combine(AppPaths.DefaultDataDirectory, "demo");

    /// <summary>"Try the demo" was chosen on the sign-in window.</summary>
    public event EventHandler? StartRequested;

    public void RequestStart() => StartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Points a fresh demo folder's settings at the demo server, with a dashboard worth looking at.</summary>
    internal static void Seed(ISettingsStore store, ITokenProtector tokens, Uri server, DemoFleet fleet, bool forScreenshots)
    {
        var settings = store.Current;
        settings.ServerUrl = server.ToString();
        settings.RememberToken = true;
        // Screenshots are always of the dark theme; a demo follows Windows, like a fresh install.
        settings.Theme = forScreenshots ? AppTheme.Dark : AppTheme.System;
        settings.WelcomeDismissed = true;
        settings.JigglePhysicsOnMaps = false;
        settings.MinimiseToTrayOnClose = false;
        settings.Notifications.SuppressOnFirstPoll = true;
        settings.Notifications.Enabled = !forScreenshots;
        settings.Window = null;
        settings.PinnedDevices = fleet.PinnedDevices
            .Select((d, i) => new PinnedDevice { DeviceId = d.Id, DisplayName = d.Name, PinnedAt = DateTimeOffset.Now.AddDays(-10 + i) })
            .ToList();
        settings.RecentlyViewedDevices = fleet.RecentlyViewed
            .Select((d, i) => new RecentlyViewedDevice { DeviceId = d.Id, DisplayName = d.Name, ViewedAt = DateTimeOffset.Now.AddMinutes(-3 - i * 7) })
            .ToList();

        // Laid out for a full 1080p screen: 45 cells across, 23 down.
        var sensors = new DashboardWidget { WidgetType = DashboardWidgetTypes.Sensors, Title = "Pinned sensors", Column = 0, Row = 8, ColumnSpan = 13, RowSpan = 8 };
        sensors.Sensors.AddRange(fleet.PinnedSensors.Select(s => new PinnedSensor { SensorId = s.SensorId, DeviceId = s.DeviceId, SensorClass = s.Class, DeviceName = s.Device, Description = s.Description }));

        settings.DashboardWidgets = new List<DashboardWidget>
        {
            new() { WidgetType = DashboardWidgetTypes.Alerts, Title = "Live alerts", Column = 0, Row = 0, ColumnSpan = 20, RowSpan = 8 },
            new() { WidgetType = DashboardWidgetTypes.AlertsGauge, Title = "Alerts", Column = 20, Row = 0, ColumnSpan = 12, RowSpan = 8 },
            new() { WidgetType = DashboardWidgetTypes.DeviceStatus, Title = "Devices", Column = 32, Row = 0, ColumnSpan = 13, RowSpan = 8 },
            sensors,
            new() { WidgetType = DashboardWidgetTypes.Graph, Title = "core-sw-01 traffic", Column = 13, Row = 8, ColumnSpan = 19, RowSpan = 8, GraphDeviceId = fleet.FeaturedDeviceId, GraphName = "device_bits" },
            new() { WidgetType = DashboardWidgetTypes.PinnedDevices, Title = "Pinned devices", Column = 32, Row = 8, ColumnSpan = 13, RowSpan = 8 },
            new() { WidgetType = DashboardWidgetTypes.TopInterfaces, Title = "Top interfaces", Column = 0, Row = 16, ColumnSpan = 20, RowSpan = 7, TopCount = 4 },
            new() { WidgetType = DashboardWidgetTypes.RecentlyViewed, Title = "Recently viewed", Column = 20, Row = 16, ColumnSpan = 12, RowSpan = 7 },
            new() { WidgetType = DashboardWidgetTypes.EventLog, Title = "Event log", Column = 32, Row = 16, ColumnSpan = 13, RowSpan = 7, LogCount = 8 },
        };

        store.Save();
        tokens.Save("demo-token-not-a-real-one");
    }

    /// <summary>Empties the demo folder, so every demo starts from the same place.</summary>
    public static void Reset()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held open: the demo seeds over what's left.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
