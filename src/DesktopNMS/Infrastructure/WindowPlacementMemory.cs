using System;
using System.Linq;
using System.Windows;
using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Remembers a resizable window's size, position and maximised state by
/// window type (#59), so Device Details, Settings and the editors reopen
/// how they were left rather than at a fixed size. Fixed-size dialogs are
/// left to centre on their owner. A second window of a type already open
/// (two Device Details) opens a little down and right of the first.
/// </summary>
public static class WindowPlacementMemory
{
    private const double CascadeOffset = 28;

    public static void Attach(Window window, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        if (window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
        {
            return;
        }

        var key = window.GetType().Name;
        if (settings.Current.WindowPlacements.TryGetValue(key, out var placement) && placement.Width >= 300 && placement.Height >= 200)
        {
            window.Width = placement.Width;
            window.Height = placement.Height;

            var (left, top) = Cascade(window, placement.Left, placement.Top);
            if (IsOnScreen(left, top))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left;
                window.Top = top;
            }

            if (placement.Maximised)
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        window.Closing += (_, _) => Save(window, key, settings);
    }

    // Down and right of any open window of the same type already there.
    private static (double Left, double Top) Cascade(Window window, double left, double top)
    {
        var others = Application.Current.Windows
            .OfType<Window>()
            .Where(w => !ReferenceEquals(w, window) && w.GetType() == window.GetType() && w.IsVisible)
            .ToList();

        for (var i = 0; i < 10 && others.Any(w => Math.Abs(w.Left - left) < 4 && Math.Abs(w.Top - top) < 4); i++)
        {
            left += CascadeOffset;
            top += CascadeOffset;
        }

        return (left, top);
    }

    // Not where a since-unplugged monitor was.
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50
        && top >= SystemParameters.VirtualScreenTop - 50
        && left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private static void Save(Window window, string key, ISettingsStore settings)
    {
        try
        {
            var maximised = window.WindowState == WindowState.Maximized;
            var bounds = maximised ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);
            if (double.IsNaN(bounds.Width) || bounds.Width < 1 || window.WindowState == WindowState.Minimized)
            {
                return;
            }

            settings.Current.WindowPlacements[key] = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximised = maximised,
            };
            settings.SaveQuietly();
        }
        catch (Exception)
        {
            // Never let remembering a window's placement block closing it.
        }
    }
}
