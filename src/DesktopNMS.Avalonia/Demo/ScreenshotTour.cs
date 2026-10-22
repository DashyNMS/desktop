using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Demo;

/// <summary>
/// Walks the demo through the main screens and saves each window as a PNG
/// (<c>--demo --screenshots &lt;folder&gt;</c>, via tools/Render-Screenshots.ps1)
/// - the real views, rendered from the example network, so the README and
/// release screenshots never show anyone's real devices (#243, #250, from the
/// WPF app's ScreenshotTour).
/// </summary>
internal sealed class ScreenshotTour(string folder, MainViewModel main, IWindowService windows, DemoFleet fleet, ILogger logger)
{
    /// <summary>Every screenshot's size, in device-independent pixels: a full 1080p screen.</summary>
    private const double Width = 1920;
    private const double Height = 1080;

    public async Task RunAsync(Window mainWindow)
    {
        Directory.CreateDirectory(folder);
        Size(mainWindow);

        // The first poll, and the fleet-wide lists behind the maps and neighbours.
        await SettleAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(true);

        await TabAsync(mainWindow, MainTab.Dashboard, "dashboard", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Devices, "devices", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Alerts, "alerts", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Rules, "alert-rules", 3).ConfigureAwait(true);
        main.Health.SelectedCategory = HealthCategory.Temperature;
        await TabAsync(mainWindow, MainTab.Health, "health", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Neighbours, "neighbours", 4).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.LogsEvents, "logs", 4).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.MapsNetwork, "network-map", 8).ConfigureAwait(true);

        windows.ShowDeviceDetail(fleet.FeaturedDeviceId);
        await SettleAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        var deviceWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.OfType<DeviceView>().FirstOrDefault();
        if (deviceWindow is { DataContext: DeviceDetailViewModel device })
        {
            Size(deviceWindow);
            await SectionAsync(deviceWindow, () => device.SelectedSection = DeviceDetailSection.Overview, "device-overview").ConfigureAwait(true);
            await SectionAsync(deviceWindow, () => device.SelectedSection = DeviceDetailSection.Ports, "device-ports").ConfigureAwait(true);
            await SectionAsync(deviceWindow, () => device.ShowGraph("device_bits"), "device-graphs").ConfigureAwait(true);
            await SectionAsync(deviceWindow, () => device.ShowGraph("device_temperature"), "device-graphs-temperature").ConfigureAwait(true);
            await SectionAsync(deviceWindow, () => { device.Graphs.Toggle("PSU 1"); device.Graphs.Toggle("PSU 2"); }, "device-graphs-legend").ConfigureAwait(true);
            await SectionAsync(deviceWindow, () => device.SelectedSection = DeviceDetailSection.Neighbours, "device-neighbours").ConfigureAwait(true);
            deviceWindow.Close();
        }
        else
        {
            logger.LogWarning("Screenshots: Device Details didn't open");
        }

        logger.LogInformation("Screenshots written to {Folder}", folder);
    }

    private async Task TabAsync(Window window, MainTab tab, string name, double seconds)
    {
        main.SelectedTab = tab;
        Size(window);
        await SettleAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(true);
        Save(window, name);
    }

    private async Task SectionAsync(Window window, Action open, string name)
    {
        open();
        Size(window);
        await SettleAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        Save(window, name);
    }

    private static void Size(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.Width = Width;
        window.Height = Height;
        window.Position = new PixelPoint(0, 0);
        window.Activate();
    }

    private static async Task SettleAsync(TimeSpan wait)
    {
        await Task.Delay(wait).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    /// <summary>One image pixel per device-independent pixel: sharp enough for the README and release notes, small enough to keep in the repo.</summary>
    private void Save(Window window, string name)
    {
        window.UpdateLayout();
        var size = window.Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
        {
            logger.LogWarning("Screenshots: nothing to render for {Name}", name);
            return;
        }

        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(size.Width), (int)Math.Round(size.Height)), new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(folder, name + ".png");
        bitmap.Save(path);
        logger.LogInformation("Screenshots: {Path} ({Width}x{Height})", path, size.Width, size.Height);
    }
}
