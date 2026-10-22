using System.Runtime.InteropServices;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.Services;

/// <summary>A native Shell balloon fallback without WinForms or System.Drawing.</summary>
public sealed class WindowsBalloonNotifier : ITrayNotifier, IDisposable
{
    private IntPtr _window;
    private IUiTimer? _expiry;
    private IntPtr _largeIcon;
    private IntPtr _smallIcon;

    public void ShowBalloon(string title, string message, AlertSeverity severity)
    {
        if (_window == IntPtr.Zero)
            _window = CreateWindowEx(0, "STATIC", "DashyNMS notifications", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) return;
        var data = Data();
        data.Flags = 2 | 4 | 16; // icon, tooltip, balloon
        if (_largeIcon == IntPtr.Zero && _smallIcon == IntPtr.Zero && Environment.ProcessPath is { } path)
            ExtractIconEx(path, 0, out _largeIcon, out _smallIcon, 1);
        data.Icon = _smallIcon != IntPtr.Zero ? _smallIcon : _largeIcon != IntPtr.Zero
            ? _largeIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512));
        data.Tip = "DashyNMS";
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = severity == AlertSeverity.Critical ? 3u : severity == AlertSeverity.Warning ? 2u : 1u;
        ShellNotifyIcon(2, ref data);
        ShellNotifyIcon(0, ref data);
        _expiry ??= UiThread.CreateTimer(TimeSpan.FromSeconds(15));
        _expiry.Tick -= OnExpired;
        _expiry.Tick += OnExpired;
        _expiry.Stop();
        _expiry.Start();
    }

    private NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _window, Id = 1, Tip = "", Info = "", InfoTitle = "" };
    private void OnExpired(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        _expiry?.Stop();
        if (_window == IntPtr.Zero) return;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        DestroyWindow(_window);
        _window = IntPtr.Zero;
        if (_largeIcon != IntPtr.Zero) DestroyIcon(_largeIcon);
        if (_smallIcon != IntPtr.Zero && _smallIcon != _largeIcon) DestroyIcon(_smallIcon);
        _largeIcon = _smallIcon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr icon);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
