using System.Globalization;

namespace DesktopNMS.Core.Intelligence;

public sealed record IntelligenceChatActivityGroup(string Label, int Order);

public static class IntelligenceChatActivityGroups
{
    /// <summary>Non-overlapping calendar buckets, using local dates and Monday-based weeks.</summary>
    public static IntelligenceChatActivityGroup ForDate(DateOnly activity, DateOnly today, bool pinned, CultureInfo? culture = null)
    {
        if (pinned) return new("Pinned", 0);
        if (activity >= today) return new("Today", 1);
        if (activity == today.AddDays(-1)) return new("Yesterday", 2);
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (activity >= weekStart) return new("This week", 3);
        if (activity >= weekStart.AddDays(-7)) return new("Last week", 4);
        if (activity >= new DateOnly(today.Year, today.Month, 1)) return new("Earlier this month", 5);
        culture ??= CultureInfo.CurrentCulture;
        return activity.Year == today.Year
            ? new(activity.ToString("MMMM", culture), 20 + 12 - activity.Month)
            : new(activity.Year.ToString(culture), 10000 + today.Year - activity.Year);
    }
}
