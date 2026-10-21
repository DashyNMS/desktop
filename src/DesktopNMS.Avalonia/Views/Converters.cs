using Avalonia.Data.Converters;
using Avalonia.Media;
using FluentIcons.Common;

namespace DesktopNMS.Views;

/// <summary>Small converters the views share (#250).</summary>
public static class Converters
{
    /// <summary>"#3B82F6" as a brush; anything unreadable as transparent.</summary>
    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush>(
        hex => Color.TryParse(hex, out var colour) ? new SolidColorBrush(colour) : Brushes.Transparent);

    /// <summary>What's New's icon names (WhatsNew.md's trailing "{name}") as Fluent icons.</summary>
    public static readonly IValueConverter WhatsNewIcon = new FuncValueConverter<string?, Symbol>(name => name switch
    {
        "map" => Symbol.Map,
        "link" => Symbol.Link,
        "logs" => Symbol.TextBulletList,
        "copy" => Symbol.Copy,
        "add" => Symbol.Add,
        "swap" => Symbol.ArrowSwap,
        "chart" => Symbol.DataLine,
        "download" => Symbol.ArrowDownload,
        "look" => Symbol.Eye,
        "grid" => Symbol.Grid,
        "tray" => Symbol.TrayItemAdd,
        "lock" => Symbol.LockClosed,
        "speed" => Symbol.TopSpeed,
        "clock" => Symbol.Clock,
        "shield" => Symbol.Shield,
        "signout" => Symbol.SignOut,
        "package" => Symbol.Box,
        "bell" => Symbol.Alert,
        "device" => Symbol.Server,
        "settings" => Symbol.Settings,
        "search" => Symbol.Search,
        "wrench" => Symbol.Wrench,
        "check" => Symbol.Checkmark,
        _ => Symbol.Star,
    });
}
