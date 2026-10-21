using Avalonia;
using Avalonia.Controls;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Remembers a resizable window's size, position and maximised state by window
/// type (#59), as the WPF app's WindowPlacementMemory does, so windows reopen
/// how they were left. Fixed-size dialogs centre on their owner.
/// </summary>
public static class WindowPlacementMemory
{
    public static void Attach(Window window, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        if (!window.CanResize)
        {
            return;
        }

        var key = window.GetType().Name;
        if (settings.Current.WindowPlacements.TryGetValue(key, out var placement) && placement.Width >= 300 && placement.Height >= 200)
        {
            window.Width = placement.Width;
            window.Height = placement.Height;
            var position = new PixelPoint((int)placement.Left, (int)placement.Top);
            if (IsOnScreen(window, position))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
            }

            if (placement.Maximised)
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        // Size and position while it's normal, so a maximised window remembers where to restore to.
        var normal = new Rect(window.Position.X, window.Position.Y, window.Width, window.Height);
        window.PositionChanged += (_, _) => normal = Track(window, normal);
        window.SizeChanged += (_, _) => normal = Track(window, normal);
        window.Closing += (_, _) => Save(window, key, settings, normal);
    }

    private static Rect Track(Window window, Rect normal)
        => window.WindowState == WindowState.Normal
            ? new Rect(window.Position.X, window.Position.Y, window.Width, window.Height)
            : normal;

    /// <summary>Not where a since-unplugged monitor was.</summary>
    private static bool IsOnScreen(Window window, PixelPoint position)
        => window.Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(position.X + 50, position.Y + 50)));

    private static void Save(Window window, string key, ISettingsStore settings, Rect normal)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized || normal.Width < 1)
            {
                return;
            }

            settings.Current.WindowPlacements[key] = new WindowPlacement
            {
                Left = normal.X,
                Top = normal.Y,
                Width = normal.Width,
                Height = normal.Height,
                Maximised = window.WindowState == WindowState.Maximized,
            };
            settings.SaveQuietly();
        }
        catch (Exception)
        {
            // Never let remembering a window's placement block closing it.
        }
    }
}
