namespace DesktopNMS.Infrastructure;

/// <summary>
/// When DashyNMS asks before acting (#66), so every view asks the same way:
/// <list type="bullet">
/// <item>Anything nothing in DashyNMS can undo - deleting from LibreNMS,
/// deleting a map, widget or neighbourhood, forgetting a stored password,
/// discarding unsaved changes, signing out - always asks, as a destructive
/// confirmation naming the thing in quotes and ending "This can't be undone."</item>
/// <item>A server action applied to a selection asks only when it covers more
/// than <see cref="BulkThreshold"/> items.</item>
/// <item>A single, reversible action (acknowledge, enable/disable one rule,
/// rediscover one device) just happens.</item>
/// </list>
/// The confirm button always names the action - see <see cref="Services.IWindowService.Confirm"/>.
/// </summary>
public static class Confirmations
{
    /// <summary>A selection larger than this asks before a bulk server action runs.</summary>
    public const int BulkThreshold = 5;

    /// <summary>The standard closing line for a destructive confirmation.</summary>
    public const string CannotBeUndone = "This can't be undone.";
}
