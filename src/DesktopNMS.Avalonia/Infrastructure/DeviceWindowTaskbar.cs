using Avalonia;
using Avalonia.Controls;

namespace DesktopNMS.Infrastructure;

/// <summary>Keep device windows off the taskbar while their main window is available.</summary>
public static class DeviceWindowTaskbar
{
    public static void Attach(Window device, Window? main)
    {
        if (main is null) return;

        void Update() => device.ShowInTaskbar = !main.IsVisible;
        void OnMainChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty) Update();
        }

        Update();
        main.PropertyChanged += OnMainChanged;
        device.Closed += (_, _) => main.PropertyChanged -= OnMainChanged;
    }
}
