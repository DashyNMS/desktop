using System;
using System.Linq;
using DesktopNMS.Core.Models;

namespace DesktopNMS.ViewModels;

/// <summary>
/// What a device row on the Dashboard shows under and beside its name (#212):
/// its status dot and "hardware · location". Shared by the Pinned devices
/// and Recently viewed widgets so their rows read the same.
/// </summary>
public static class DeviceRowDetails
{
    /// <summary>"C9300-48P · DC1 Rack 3", either part alone, or null when neither is known.</summary>
    public static string? Subtitle(Device? device)
    {
        if (device is null)
        {
            return null;
        }

        var parts = new[] { device.Hardware, device.Location }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"just now", "5m ago", "3h ago", "2d ago".</summary>
    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d ago"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h ago"
            : $"{(int)span.TotalMinutes}m ago";
    }
}
