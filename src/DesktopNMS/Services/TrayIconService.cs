using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using System.Windows.Interop;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
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
/// the notification area from a WPF app without a third-party dependency -
/// but only for the icon itself. Its menu and quick look (#232) are WPF, in
/// the app's theme, bound to <see cref="TrayViewModel"/>. The icon is drawn
/// at runtime so it can track the state without shipping a set of .ico files.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class TrayIconService : ITrayNotifier, IDisposable
{
    private static readonly Color RingColour = Color.FromArgb(59, 130, 246);
    private static readonly Color FaceColour = Color.FromArgb(16, 19, 26);
    private static readonly Color PulseColour = Color.FromArgb(232, 238, 246);
    private static readonly Color WarningColour = Color.FromArgb(219, 154, 4);
    private static readonly Color CriticalColour = Color.FromArgb(218, 54, 51);
    private static readonly Color DisconnectedColour = Color.FromArgb(87, 96, 106);

    /// <summary>A click within this long of the quick look closing is the click that closed it.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly ILogger<TrayIconService> _logger;
    private readonly TrayViewModel _viewModel;

    private NotifyIcon? _notifyIcon;
    private TrayMenu? _menu;
    private TrayFlyoutWindow? _flyout;
    private bool _disposed;

    // Icon.FromHandle borrows the HICON rather than owning it, so the handle
    // has to outlive every use of the Icon built from it. These track the icon
    // currently on screen so the previous one is only freed after the shell has
    // been handed its replacement.
    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;

    /// <summary>What the on-screen icon depicts, so it is only redrawn when it changes.</summary>
    private (TrayIconKind Kind, int Count)? _renderedIcon;

    public TrayIconService(ILogger<TrayIconService> logger, TrayViewModel viewModel)
    {
        _logger = logger;
        _viewModel = viewModel;
    }

    /// <summary>The menu or quick look is about to open: a chance to bring the view model up to date.</summary>
    public event EventHandler? Opening;

    public void Initialise()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        _notifyIcon = new NotifyIcon
        {
            Text = _viewModel.Tooltip,
            Visible = true,
        };
        Repaint();

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleFlyout();
            }
        };
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _flyout?.HideFlyout();
                _viewModel.OpenCommand.Execute(null);
            }
        };
        _notifyIcon.BalloonTipClicked += (_, _) => _viewModel.OpenCommand.Execute(null);

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.CloseRequested += (_, _) =>
        {
            _flyout?.HideFlyout();
            if (_menu is not null)
            {
                _menu.IsOpen = false;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(TrayViewModel.IconKind) or nameof(TrayViewModel.BadgeCount) or nameof(TrayViewModel.Tooltip))
        {
            Repaint();
        }
    }

    private void ShowMenu()
    {
        try
        {
            _flyout?.HideFlyout();
            Opening?.Invoke(this, EventArgs.Empty);

            if (_menu is null)
            {
                _menu = new TrayMenu { DataContext = _viewModel };

                // As with the quick look: without being the foreground app,
                // clicking elsewhere wouldn't close the menu.
                _menu.Opened += (_, _) =>
                {
                    if (System.Windows.PresentationSource.FromVisual(_menu) is HwndSource source)
                    {
                        SetForegroundWindow(source.Handle);
                    }
                };
            }

            _menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the tray menu");
        }
    }

    private void ToggleFlyout()
    {
        try
        {
            if (_menu is { IsOpen: true })
            {
                _menu.IsOpen = false;
            }

            _flyout ??= new TrayFlyoutWindow { DataContext = _viewModel };

            if (_flyout.IsVisible)
            {
                _flyout.HideFlyout();
                return;
            }

            if (DateTime.UtcNow - _flyout.LastHidden < ReopenGuard)
            {
                return;
            }

            Opening?.Invoke(this, EventArgs.Empty);
            _flyout.ShowNear(Cursor.Position);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the tray quick look");
        }
    }

    /// <summary>Repaints the icon and tooltip from the view model.</summary>
    private void Repaint()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            // Repainting only when the picture actually changes keeps this off
            // the GDI-handle treadmill: it used to run on every poll.
            var wanted = (_viewModel.IconKind, _viewModel.BadgeCount);
            if (_renderedIcon != wanted)
            {
                var (newIcon, newHandle) = CreateIcon(wanted.IconKind, wanted.BadgeCount);

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

            _notifyIcon.Text = _viewModel.Tooltip;
        }
        catch (Exception ex)
        {
            // A failed repaint must not take the tray icon down with it.
            _logger.LogWarning(ex, "Could not update the tray icon");
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
    /// The icon for a state (#232): DashyNMS's mark - a pulse line on a dark
    /// disc inside a blue ring - while all is well, with a small amber dot on
    /// the backup address, and grey while not connected. Active alerts turn
    /// the whole icon into a solid amber or red dot with the count in it, as
    /// before #183, so it stands out in a crowded notification area.
    /// </summary>
    /// <returns>
    /// The icon and the HICON backing it. The caller owns the handle and must
    /// free it with <see cref="DestroyIcon"/> once the icon is off screen:
    /// <see cref="Icon.FromHandle"/> borrows the handle rather than copying it,
    /// so freeing it early leaves the shell drawing from destroyed memory.
    /// </returns>
    private static (Icon Icon, IntPtr Handle) CreateIcon(TrayIconKind kind, int count)
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

            var inset = canvas * 0.04f;
            var disc = canvas - (inset * 2);

            if (kind is TrayIconKind.Warning or TrayIconKind.Critical)
            {
                using var brush = new SolidBrush(kind == TrayIconKind.Critical ? CriticalColour : WarningColour);
                graphics.FillEllipse(brush, inset, inset, disc, disc);

                if (count > 0)
                {
                    var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                    var fontSize = text.Length switch
                    {
                        1 => canvas * 0.62f,
                        2 => canvas * 0.50f,
                        _ => canvas * 0.36f,
                    };

                    // Segoe UI, not the bundled brand fonts (#216): this is
                    // GDI drawing the tray icon, which only sees installed fonts.
                    using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                    using var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                    };

                    graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 0, canvas, canvas), format);
                }
            }
            else
            {
                DrawMark(graphics, inset, inset, disc, connected: kind != TrayIconKind.NotConnected);
            }

            if (kind == TrayIconKind.Backup)
            {
                // A dot at the top right, ringed in the face colour so it
                // reads against the blue ring under it.
                var dot = canvas * 0.42f;
                var outline = canvas * 0.06f;
                using (var face = new SolidBrush(FaceColour))
                {
                    graphics.FillEllipse(face, canvas - dot - outline, 0, dot + outline, dot + outline);
                }

                using var amber = new SolidBrush(WarningColour);
                graphics.FillEllipse(amber, canvas - dot - (outline / 2), outline / 2, dot, dot);
            }
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private static void DrawMark(Graphics graphics, float left, float top, float disc, bool connected)
    {
        var ring = Math.Max(1.5f, disc * 0.073f);

        using (var face = new SolidBrush(FaceColour))
        {
            graphics.FillEllipse(face, left, top, disc, disc);
        }

        using (var pen = new Pen(connected ? RingColour : DisconnectedColour, ring))
        {
            graphics.DrawEllipse(pen, left + (ring / 2), top + (ring / 2), disc - ring, disc - ring);
        }

        // The pulse, from the mockup's 22-unit drawing of it.
        var unit = disc / 22f;
        var pulse = new[] { (4f, 12f), (8f, 12f), (10.5f, 7f), (13.5f, 16f), (16f, 10f), (18f, 10f) }
            .Select(p => new PointF(left + (p.Item1 * unit), top + (p.Item2 * unit)))
            .ToArray();

        using var line = new Pen(connected ? PulseColour : DisconnectedColour, Math.Max(1.5f, 1.9f * unit))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLines(line, pulse);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelChanged;

        if (_menu is not null)
        {
            _menu.IsOpen = false;
            _menu = null;
        }

        _flyout?.Close();
        _flyout = null;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
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
