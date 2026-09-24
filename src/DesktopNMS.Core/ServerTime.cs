namespace DesktopNMS.Core;

/// <summary>
/// Turns a timestamp from LibreNMS into this PC's local time. LibreNMS's
/// MySQL datetime strings carry no zone, and whether they're in UTC or the
/// server's own time depends on how the server is set up - which is what
/// Settings, "Server stores timestamps in UTC" says (#150). Unix timestamps
/// (e.g. outages) are unambiguous, and arrive already marked as UTC.
/// </summary>
public static class ServerTime
{
    /// <param name="value">A parsed LibreNMS timestamp.</param>
    /// <param name="serverTimestampsAreUtc">The Settings option - only consulted for a timestamp with no zone of its own.</param>
    public static DateTime ToLocal(DateTime value, bool serverTimestampsAreUtc) => value.Kind switch
    {
        DateTimeKind.Utc => value.ToLocalTime(),
        DateTimeKind.Local => value,

        // No zone: UTC if the server says so, otherwise already the
        // server's (and, the usual case, this PC's) local time. Never
        // ToLocalTime() here - .NET treats an unzoned value as UTC for that.
        _ => serverTimestampsAreUtc
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Local),
    };

    /// <inheritdoc cref="ToLocal(DateTime, bool)"/>
    public static DateTime? ToLocal(DateTime? value, bool serverTimestampsAreUtc) =>
        value is { } v ? ToLocal(v, serverTimestampsAreUtc) : null;

    /// <summary>How long ago a LibreNMS timestamp was, never negative (a server clock slightly ahead of this PC's would otherwise give "-2s ago").</summary>
    public static TimeSpan Age(DateTime value, bool serverTimestampsAreUtc)
    {
        var age = DateTime.Now - ToLocal(value, serverTimestampsAreUtc);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
