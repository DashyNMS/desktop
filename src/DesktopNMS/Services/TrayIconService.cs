using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>The balloon-tip fallback used when Windows toasts are unavailable.</summary>
public interface ITrayNotifier
{
    void ShowBalloon(string title, string message, AlertSeverity severity);
}

/// <summary>
/// The notification-area icon: the app's home while the window is closed.
/// </summary>
/// <remarks>
/// Uses WinForms NotifyIcon, which is the only supported way to put an icon in
/// the notification area from a WPF app without a third-party dependency. The
/// icon itself is drawn at runtime so its colour can track the worst
/// outstanding severity without shipping a set of .ico files.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class TrayIconService : ITrayNotifier, IDisposable
{
    private static readonly Color OkColour = Color.FromArgb(46, 160, 67);
    private static readonly Color WarningColour = Color.FromArgb(219, 154, 4);
    private static readonly Color CriticalColour = Color.FromArgb(218, 54, 51);
    private static readonly Color IdleColour = Color.FromArgb(125, 133, 144);
    private static readonly Color DisconnectedColour = Color.FromArgb(87, 96, 106);

    private readonly ILogger<TrayIconService> _logger;

    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _signOutItem;
    private bool _disposed;

    // Icon.FromHandle borrows the HICON rather than owning it, so the handle
    // has to outlive every use of the Icon built from it. These track the icon
    // currently on screen so the previous one is only freed after the shell has
    // been handed its replacement.
    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;

    /// <summary>What the on-screen icon depicts, so it is only redrawn when it changes.</summary>
    private (Color Colour, int Badge)? _renderedIcon;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? DevicesRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? SignOutRequested;

    public event EventHandler? ExitRequested;

    public void Initialise()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Not connected") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("Open DashyNMS");
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(openItem);

        var refreshItem = new ToolStripMenuItem("Refresh now");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(refreshItem);

        var devicesItem = new ToolStripMenuItem("Devices...");
        devicesItem.Click += (_, _) => DevicesRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(devicesItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settingsItem);

        _signOutItem = new ToolStripMenuItem("Sign out");
        _signOutItem.Click += (_, _) => SignOutRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_signOutItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        // Bold the default action so the menu reads the way Windows tray menus do.
        menu.Font = new Font(menu.Font, System.Drawing.FontStyle.Regular);
        openItem.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);

        (_currentIcon, _currentIconHandle) = CreateIcon(DisconnectedColour, null);
        _renderedIcon = (DisconnectedColour, 0);

        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "DashyNMS",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Repaints the icon and tooltip to reflect the current alert counts.</summary>
    public void UpdateStatus(bool connected, int criticalCount, int warningCount, string? statusLine)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var badgeCount = criticalCount + warningCount;

        var colour = !connected
            ? DisconnectedColour
            : criticalCount > 0
                ? CriticalColour
                : warningCount > 0
                    ? WarningColour
                    : OkColour;

        var tooltip = !connected
            ? "DashyNMS - not connected"
            : badgeCount == 0
                ? "DashyNMS - no active alerts"
                : $"DashyNMS - {criticalCount} critical, {warningCount} warning";

        // NotifyIcon.Text throws above 63 characters on some Windows builds.
        if (tooltip.Length > 63)
        {
            tooltip = tooltip[..63];
        }

        try
        {
            // Repainting only when the picture actually changes keeps this off
            // the GDI-handle treadmill: it used to run on every poll.
            var wanted = (colour, badgeCount);

            if (_renderedIcon != wanted)
            {
                var (newIcon, newHandle) = CreateIcon(colour, badgeCount > 0 ? badgeCount : null);

                var oldIcon = _currentIcon;
                var oldHandle = _currentIconHandle;

                _currentIcon = newIcon;
                _currentIconHandle = newHandle;
                _notifyIcon.Icon = newIcon;
                _renderedIcon = wanted;

                // Only now that the shell has the replacement is it safe to let
                // the previous icon and its handle go.
                oldIcon?.Dispose();
                if (oldHandle != IntPtr.Zero)
                {
                    DestroyIcon(oldHandle);
                }
            }

            _notifyIcon.Text = tooltip;
        }
        catch (Exception ex)
        {
            // A failed repaint must not take the tray icon down with it.
            _logger.LogWarning(ex, "Could not update the tray icon");
        }

        if (_statusItem is not null)
        {
            _statusItem.Text = statusLine ?? tooltip;
        }

        if (_signOutItem is not null)
        {
            _signOutItem.Enabled = connected;
        }
    }

    public void ShowBalloon(string title, string message, AlertSeverity severity)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = severity switch
            {
                AlertSeverity.Critical => ToolTipIcon.Error,
                AlertSeverity.Warning => ToolTipIcon.Warning,
                _ => ToolTipIcon.Info,
            };

            _notifyIcon.ShowBalloonTip(10000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not show a tray balloon");
        }
    }

    /// <summary>
    /// Draws a filled circle, optionally with a count on it, and converts it to
    /// an <see cref="Icon"/>.
    /// </summary>
    /// <returns>
    /// The icon and the HICON backing it. The caller owns the handle and must
    /// free it with <see cref="DestroyIcon"/> once the icon is off screen:
    /// <see cref="Icon.FromHandle"/> borrows the handle rather than copying it,
    /// so freeing it early leaves the shell drawing from destroyed memory.
    /// </returns>
    private static (Icon Icon, IntPtr Handle) CreateIcon(Color colour, int? badgeCount)
    {
        var size = SystemInformation.SmallIconSize.Width;
        if (size < 16)
        {
            size = 16;
        }

        // Draw at 2x and let the shell downscale: cheaper than fighting GDI+
        // hinting at 16 pixels.
        var canvas = size * 2;

        using var bitmap = new Bitmap(canvas, canvas);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            var inset = canvas * 0.06f;
            var diameter = canvas - (inset * 2);

            using var brush = new SolidBrush(colour);
            graphics.FillEllipse(brush, inset, inset, diameter, diameter);

            if (badgeCount is > 0)
            {
                var text = badgeCount.Value > 99
                    ? "99+"
                    : badgeCount.Value.ToString(CultureInfo.InvariantCulture);

                var fontSize = text.Length switch
                {
                    1 => canvas * 0.62f,
                    2 => canvas * 0.50f,
                    _ => canvas * 0.36f,
                };

                using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };

                graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 0, canvas, canvas), format);
            }
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _currentIcon?.Dispose();
        _currentIcon = null;

        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
