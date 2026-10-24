using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>Keep past incident cycles in history rather than the current entity fault list.</summary>
public static class CurrentAlertIncidents
{
    public static IReadOnlyList<Alert> Select(IReadOnlyList<Alert> alerts)
        => alerts.GroupBy(alert => (alert.DeviceId, alert.RuleId, alert.Source,
                Entity: alert.Source == AlertSource.Fault ? alert.EntityKey : null,
                Incident: alert.Source == AlertSource.Fault && alert.EntityKey is not null ? (int?)null : alert.Id))
            .SelectMany(group =>
            {
                // Keep every outstanding row: presentation must never hide a live fault.
                var outstanding = group.Where(alert => alert.State != AlertState.Recovered).ToList();
                return outstanding.Count > 0 ? outstanding : new[] { group.MaxBy(alert => alert.Id)! }.AsEnumerable();
            }).ToList();
}
