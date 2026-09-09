using DesktopNMS.Core.Alerting;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class SelfActionTrackerTests
{
    [Fact]
    public void Reports_a_just_recorded_action_as_self_initiated()
    {
        var tracker = new SelfActionTracker();

        tracker.Record(42, AlertChangeKind.Acknowledged);

        Assert.True(tracker.WasSelfInitiated(42, AlertChangeKind.Acknowledged));
    }

    [Fact]
    public void Is_consumed_by_a_single_check()
    {
        var tracker = new SelfActionTracker();
        tracker.Record(42, AlertChangeKind.Acknowledged);

        Assert.True(tracker.WasSelfInitiated(42, AlertChangeKind.Acknowledged));
        Assert.False(tracker.WasSelfInitiated(42, AlertChangeKind.Acknowledged));
    }

    [Fact]
    public void Does_not_match_a_different_kind()
    {
        var tracker = new SelfActionTracker();
        tracker.Record(42, AlertChangeKind.Acknowledged);

        Assert.False(tracker.WasSelfInitiated(42, AlertChangeKind.Unacknowledged));
    }

    [Fact]
    public void Unrecorded_alert_is_never_self_initiated()
    {
        var tracker = new SelfActionTracker();

        Assert.False(tracker.WasSelfInitiated(99, AlertChangeKind.Acknowledged));
    }

    [Fact]
    public void Recording_again_overwrites_the_previous_kind()
    {
        var tracker = new SelfActionTracker();

        tracker.Record(42, AlertChangeKind.Acknowledged);
        tracker.Record(42, AlertChangeKind.Unacknowledged);

        Assert.False(tracker.WasSelfInitiated(42, AlertChangeKind.Acknowledged));
    }
}
