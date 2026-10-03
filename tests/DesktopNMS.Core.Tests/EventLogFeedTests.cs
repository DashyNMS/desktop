using DesktopNMS.Core.Logs;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class EventLogFeedTests
{
    private static readonly EventLogEntry[] Entries =
    {
        new() { Id = 1, Type = "system", Message = "Device rebooted", Hostname = "sw-core-01", Timestamp = new DateTime(2026, 10, 1, 9, 0, 0) },
        new() { Id = 2, Type = "interface", Message = "Gi1/0/24 went down", Hostname = "sw-access-12", Timestamp = new DateTime(2026, 10, 1, 9, 5, 0) },
        new() { Id = 3, Type = "Interface", Message = "Gi1/0/24 came up", Hostname = "sw-access-12", SysName = "dist-07", Timestamp = new DateTime(2026, 10, 1, 9, 6, 0) },
        new() { Id = 4, Type = "sensor", Message = "Temperature crossed the warning limit", Hostname = "192.0.2.10", Timestamp = null },
    };

    [Fact]
    public void Newest_first_and_capped()
        => Assert.Equal(new[] { 3, 2 }, EventLogFeed.Filter(Entries, null, null, 2).Select(e => e.Id));

    [Fact]
    public void Type_matches_any_case()
        => Assert.Equal(new[] { 3, 2 }, EventLogFeed.Filter(Entries, "interface", null, 10).Select(e => e.Id));

    [Fact]
    public void Search_looks_at_message_type_hostname_and_sysName()
    {
        Assert.Equal(new[] { 1 }, EventLogFeed.Filter(Entries, null, "REBOOT", 10).Select(e => e.Id));
        Assert.Equal(new[] { 3, 2 }, EventLogFeed.Filter(Entries, null, "access-12", 10).Select(e => e.Id));
        Assert.Equal(new[] { 3 }, EventLogFeed.Filter(Entries, null, "dist-07", 10).Select(e => e.Id));
        Assert.Equal(new[] { 4 }, EventLogFeed.Filter(Entries, null, "192.0.2.10", 10).Select(e => e.Id));
        Assert.Equal(new[] { 4 }, EventLogFeed.Filter(Entries, null, "sensor", 10).Select(e => e.Id));
    }

    [Fact]
    public void Types_are_distinct_whatever_their_case_and_sorted()
        => Assert.Equal(new[] { "interface", "sensor", "system" }, EventLogFeed.Types(Entries));

    [Fact]
    public void Fetches_deeper_only_when_filtering()
    {
        Assert.Equal(10, EventLogFeed.FetchLimit(10, null, " "));
        Assert.Equal(EventLogFeed.FilteredFetchLimit, EventLogFeed.FetchLimit(10, "system", null));
        Assert.Equal(EventLogFeed.FilteredFetchLimit, EventLogFeed.FetchLimit(10, null, "down"));
    }
}
