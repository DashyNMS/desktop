using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Demo;

/// <summary>
/// Walks the demo through the main screens and saves each window as a PNG
/// (<c>--demo --screenshots &lt;folder&gt;</c>, via tools/Render-Screenshots.ps1) -
/// the real views, rendered from the example network, so the README and
/// release screenshots never show anyone's real devices.
/// </summary>
internal sealed class ScreenshotTour
{
    /// <summary>Every screenshot's size, in device-independent pixels: a full 1080p screen.</summary>
    private const double Width = 1920;
    private const double Height = 1080;

    /// <summary>One image pixel per DIP: sharp enough for the README and release notes, small enough to keep in the repo.</summary>
    private const double Scale = 1;

    private readonly string _folder;
    private readonly MainViewModel _main;
    private readonly IWindowService _windows;
    private readonly DemoFleet _fleet;
    private readonly ILogger _logger;

    public ScreenshotTour(string folder, MainViewModel main, IWindowService windows, DemoFleet fleet, ILogger logger)
    {
        _folder = folder;
        _main = main;
        _windows = windows;
        _fleet = fleet;
        _logger = logger;
    }

    public async Task RunAsync(Window mainWindow)
    {
        Directory.CreateDirectory(_folder);
        Size(mainWindow);

        // The first poll, and the fleet-wide lists behind the maps and neighbours.
        await SettleAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(true);

        await TabAsync(mainWindow, MainTab.Dashboard, "dashboard", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Devices, "devices", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Alerts, "alerts", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Rules, "alert-rules", 3).ConfigureAwait(true);
        _main.Health.SelectedCategory = HealthCategory.Temperature;
        await TabAsync(mainWindow, MainTab.Health, "health", 3).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.Neighbours, "neighbours", 4).ConfigureAwait(true);
        await TabAsync(mainWindow, MainTab.MapsNetwork, "network-map", 8).ConfigureAwait(true);

        _windows.ShowDeviceDetail(_fleet.FeaturedDeviceId);
        await SettleAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        if (Application.Current.Windows.OfType<DeviceView>().FirstOrDefault() is { DataContext: DeviceDetailViewModel device } deviceWindow)
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
            _logger.LogWarning("Screenshots: Device Details didn't open");
        }

        _logger.LogInformation("Screenshots written to {Folder}", _folder);
    }

    private async Task TabAsync(Window window, MainTab tab, string name, double seconds)
    {
        _main.SelectedTab = tab;
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
        window.Left = 0;
        window.Top = 0;
        window.Activate();
    }

    private static async Task SettleAsync(TimeSpan wait)
    {
        await Task.Delay(wait).ConfigureAwait(true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private void Save(Window window, string name)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
        {
            _logger.LogWarning("Screenshots: nothing to render for {Name}", name);
            return;
        }

        var width = root.ActualWidth;
        var height = root.ActualHeight;

        // The window's own background first: the content above it is partly see-through.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap((int)Math.Round(width * Scale), (int)Math.Round(height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(_folder, name + ".png");
        using var file = File.Create(path);
        encoder.Save(file);
        _logger.LogInformation("Screenshots: {Path} ({Width}x{Height}, window {State})", path, width, height, window.WindowState);
    }
}
