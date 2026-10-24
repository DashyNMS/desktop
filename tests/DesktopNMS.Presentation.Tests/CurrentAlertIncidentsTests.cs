using DesktopNMS.Core.Models;
using DesktopNMS.Services;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class CurrentAlertIncidentsTests
{
    private static Alert Fault(int id, string? key, int state = 0) => new()
    {
        Id = id, DeviceId = 7, RuleId = 3, Source = AlertSource.Fault, EntityKey = key, StateValue = state,
    };

    [Fact]
    public void Earlier_cycles_do_not_expand_the_current_fault_count()
    {
        var rows = new[] { Fault(1, "processor:1"), Fault(2, "processor:1"), Fault(3, "processor:1", 1),
            Fault(4, "processor:2", 2), Fault(5, "processor:2") };
        var current = CurrentAlertIncidents.Select(rows);
        Assert.Equal(new[] { 3, 4 }, current.Select(row => row.Id));
        Assert.Equal(5, rows.Length); // History input remains untouched.
    }

    [Fact]
    public void Recovered_entities_keep_only_the_latest_incident_even_if_older_rows_changed_later()
    {
        var old = Fault(1, "port:1"); old.Timestamp = new DateTime(2026, 10, 24);
        var latest = Fault(2, "port:1"); latest.Timestamp = new DateTime(2026, 10, 23);
        Assert.Same(latest, Assert.Single(CurrentAlertIncidents.Select(new[] { old, latest })));
    }

    [Fact]
    public void Distinct_devices_rules_unidentified_faults_and_legacy_alerts_remain_separate()
    {
        var otherDevice = Fault(2, "port:1"); otherDevice.DeviceId = 8;
        var otherRule = Fault(3, "port:1"); otherRule.RuleId = 4;
        var legacy = Fault(6, "port:1"); legacy.Source = AlertSource.Alert;
        var rows = new[] { Fault(1, "port:1"), otherDevice, otherRule, Fault(4, null), Fault(5, null), legacy };
        Assert.Equal(6, CurrentAlertIncidents.Select(rows).Count);
        Assert.Equal(2, CurrentAlertIncidents.Select(new[] { Fault(7, "same", 1), Fault(8, "same", 1) }).Count);
        Assert.Equal(9, Assert.Single(CurrentAlertIncidents.Select(new[] { Fault(8, ""), Fault(9, "") })).Id);
    }
}
