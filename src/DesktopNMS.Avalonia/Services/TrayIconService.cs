using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using DesktopNMS.Core.Alerting;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Services;

/// <summary>
/// The tray icon (#252, from the WPF app's TrayIconService, now Avalonia's
/// own TrayIcon rather than WinForms): the notification-area icon on Windows,
/// a menu-bar item on macOS and a StatusNotifierItem on Linux. A left click
/// opens the quick look (<see cref="TrayFlyoutWindow"/>) on Windows and
/// Linux; the menu is the platform's own, so it looks right on each. The icon
/// is drawn at run time so it follows the state without a set of image files.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly Color RingColour = Color.FromRgb(59, 130, 246);
    private static readonly Color FaceColour = Color.FromRgb(16, 19, 26);
    private static readonly Color PulseColour = Color.FromRgb(232, 238, 246);
    private static readonly Color WarningColour = Color.FromRgb(219, 154, 4);
    private static readonly Color CriticalColour = Color.FromRgb(218, 54, 51);
    private static readonly Color DisconnectedColour = Color.FromRgb(87, 96, 106);

    /// <summary>The icon's drawing size: the platform scales it down to fit.</summary>
    private const int IconPixels = 64;

    /// <summary>A click within this long of the quick look closing is the click that closed it.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly ILogger<TrayIconService> _logger;
    private readonly TrayViewModel _viewModel;

    private TrayIcon? _icon;
    private TrayFlyoutWindow? _flyout;
    private NativeMenuItem? _alertsItem;
    private NativeMenuItem? _refreshItem;
    private NativeMenuItem? _updateItem;
    private NativeMenuItem? _signInOrOutItem;
    private (TrayIconKind Kind, int Count)? _renderedIcon;

    public TrayIconService(ILogger<TrayIconService> logger, TrayViewModel viewModel)
    {
        _logger = logger;
        _viewModel = viewModel;
    }

    /// <summary>The menu or quick look is about to open: a chance to bring the view model up to date.</summary>
    public event EventHandler? Opening;

    /// <summary>Whether the icon is up - false when the platform has nowhere to put it.</summary>
    public bool IsShown => _icon is { IsVisible: true };

    public void Initialise(Application app)
    {
        if (_icon is not null)
        {
            return;
        }

        try
        {
            _icon = new TrayIcon { ToolTipText = _viewModel.Tooltip, Menu = BuildMenu(), IsVisible = true };
            _icon.Clicked += (_, _) => ToggleFlyout();
            TrayIcon.SetIcons(app, [_icon]);
            Repaint();

            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.CloseRequested += (_, _) => _flyout?.HideFlyout();
        }
        catch (Exception ex)
        {
            // No tray (some Linux desktops): the app carries on without one.
            _logger.LogWarning(ex, "Could not show the tray icon");
            _icon = null;
        }
    }

    /// <summary>The platform's own menu, as the WPF app's: open, the main tabs, refresh, settings, sign in or out, exit.</summary>
    private NativeMenu BuildMenu()
    {
        static NativeMenuItem Item(string header, System.Windows.Input.ICommand command) => new(header) { Command = command };

        _alertsItem = Item("Alerts", _viewModel.AlertsCommand);
        _refreshItem = Item(_viewModel.RefreshText, _viewModel.RefreshCommand);
        _updateItem = Item("Restart to update", _viewModel.InstallUpdateCommand);
        _signInOrOutItem = Item(_viewModel.SignInOrOutText, _viewModel.SignInOrOutCommand);

        var menu = new NativeMenu
        {
            Item("Open DashyNMS", _viewModel.OpenCommand),
            new NativeMenuItemSeparator(),
            Item("Dashboard", _viewModel.DashboardCommand),
            _alertsItem,
            Item("Devices", _viewModel.DevicesCommand),
            new NativeMenuItemSeparator(),
            _refreshItem,
            _updateItem,
            Item("Settings…", _viewModel.SettingsCommand),
            _signInOrOutItem,
            new NativeMenuItemSeparator(),
            Item(OperatingSystem.IsMacOS() ? "Quit DashyNMS" : "Exit", _viewModel.ExitCommand),
        };

        // Up to date as it opens - the counts and words move on between polls.
        menu.NeedsUpdate += (_, _) =>
        {
            _flyout?.HideFlyout();
            Opening?.Invoke(this, EventArgs.Empty);
            UpdateMenu();
        };

        UpdateMenu();
        return menu;
    }

    private void UpdateMenu()
    {
        if (_alertsItem is null)
        {
            return;
        }

        _alertsItem.Header = string.IsNullOrEmpty(_viewModel.AlertsCountText) ? "Alerts" : $"Alerts ({_viewModel.AlertsCountText})";
        _refreshItem!.Header = _viewModel.RefreshText;
        _updateItem!.IsVisible = _viewModel.IsUpdateReady;
        _signInOrOutItem!.Header = _viewModel.SignInOrOutText;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(TrayViewModel.IconKind) or nameof(TrayViewModel.BadgeCount) or nameof(TrayViewModel.Tooltip))
        {
            Repaint();
        }

        UpdateMenu();
    }

    /// <summary>
    /// The quick look, beside the icon - on Windows and Linux. A Mac's
    /// menu-bar item opens its menu on a click instead, as Mac ones do.
    /// </summary>
    private void ToggleFlyout()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
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
            _flyout.ShowNear(CursorPosition());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the tray quick look");
        }
    }

    /// <summary>Where the pointer is on screen, so the quick look opens by the icon - Windows only; elsewhere the quick look finds a corner.</summary>
    private static PixelPoint? CursorPosition()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var point))
        {
            return new PixelPoint(point.X, point.Y);
        }

        return null;
    }

    /// <summary>Repaints the icon and tooltip from the view model, only when the picture actually changes.</summary>
    private void Repaint()
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            var wanted = (_viewModel.IconKind, _viewModel.BadgeCount);
            if (_renderedIcon != wanted)
            {
                _icon.Icon = new WindowIcon(Draw(wanted.IconKind, wanted.BadgeCount));
                _renderedIcon = wanted;
            }

            _icon.ToolTipText = _viewModel.Tooltip;
        }
        catch (Exception ex)
        {
            // A failed repaint must not take the tray icon down with it.
            _logger.LogWarning(ex, "Could not update the tray icon");
        }
    }

    /// <summary>
    /// The icon for a state (#232): DashyNMS's mark - a pulse line on a dark
    /// disc inside a blue ring - while all is well, with a small amber dot on
    /// the backup address, and grey while not connected. Active alerts turn
    /// the whole icon into a solid amber or red dot with the count in it, so
    /// it stands out in a crowded tray.
    /// </summary>
    private static Bitmap Draw(TrayIconKind kind, int count)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(IconPixels, IconPixels));
        using (var context = bitmap.CreateDrawingContext())
        {
            const double canvas = IconPixels;
            var inset = canvas * 0.04;
            var disc = canvas - (inset * 2);
            var centre = new Point(canvas / 2, canvas / 2);

            if (kind is TrayIconKind.Warning or TrayIconKind.Critical)
            {
                context.DrawEllipse(new ImmutableSolidColorBrush(kind == TrayIconKind.Critical ? CriticalColour : WarningColour), null, centre, disc / 2, disc / 2);

                if (count > 0)
                {
                    var text = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                    var size = text.Length switch
                    {
                        1 => canvas * 0.62,
                        2 => canvas * 0.50,
                        _ => canvas * 0.36,
                    };
                    var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size, Brushes.White);
                    context.DrawText(label, new Point(centre.X - (label.Width / 2), centre.Y - (label.Height / 2)));
                }
            }
            else
            {
                DrawMark(context, inset, disc, connected: kind != TrayIconKind.NotConnected);
            }

            if (kind == TrayIconKind.Backup)
            {
                // A dot at the top right, ringed in the face colour so it reads against the blue ring under it.
                var dot = canvas * 0.42;
                var outline = canvas * 0.06;
                context.DrawEllipse(new ImmutableSolidColorBrush(FaceColour), null, new Rect(canvas - dot - outline, 0, dot + outline, dot + outline));
                context.DrawEllipse(new ImmutableSolidColorBrush(WarningColour), null, new Rect(canvas - dot - (outline / 2), outline / 2, dot, dot));
            }
        }

        return bitmap;
    }

    private static void DrawMark(DrawingContext context, double inset, double disc, bool connected)
    {
        var ring = Math.Max(1.5, disc * 0.073);
        var centre = new Point(inset + (disc / 2), inset + (disc / 2));

        context.DrawEllipse(new ImmutableSolidColorBrush(FaceColour), null, centre, disc / 2, disc / 2);
        context.DrawEllipse(null, new ImmutablePen(new ImmutableSolidColorBrush(connected ? RingColour : DisconnectedColour), ring), centre, (disc - ring) / 2, (disc - ring) / 2);

        // The pulse, from the mockup's 22-unit drawing of it.
        var unit = disc / 22;
        var points = new[] { (4.0, 12.0), (8.0, 12.0), (10.5, 7.0), (13.5, 16.0), (16.0, 10.0), (18.0, 10.0) }
            .Select(p => new Point(inset + (p.Item1 * unit), inset + (p.Item2 * unit)))
            .ToArray();

        var pulse = new StreamGeometry();
        using (var figure = pulse.Open())
        {
            figure.BeginFigure(points[0], isFilled: false);
            foreach (var point in points.Skip(1))
            {
                figure.LineTo(point);
            }

            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new ImmutablePen(new ImmutableSolidColorBrush(connected ? PulseColour : DisconnectedColour), Math.Max(1.5, 1.9 * unit),
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), pulse);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _flyout?.Close();
        _flyout = null;

        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
