using DesktopNMS.Core.Models;

namespace DesktopNMS.Services;

/// <summary>Keep past incident cycles in history rather than the current entity fault list.</summary>
public static class CurrentAlertIncidents
{
    public static IReadOnlyList<Alert> Select(IReadOnlyList<Alert> alerts)
        => alerts.GroupBy(alert => (alert.DeviceId, alert.RuleId, alert.Source))
            .SelectMany(group =>
            {
                // Recovered siblings belong to history even when their entity keys differ
                // (including whole-device rows migrated from the old alerts table).
                var outstanding = group.Where(alert => alert.State != AlertState.Recovered).ToList();
                if (outstanding.Count > 0)
                    return outstanding;

                var recovered = group.AsEnumerable();
                if (group.Key.Source == AlertSource.Fault && group.Any(alert => !string.IsNullOrEmpty(alert.EntityKey)))
                    recovered = recovered.Where(alert => alert.EntityKey != string.Empty);

                return recovered.GroupBy(alert => (
                Entity: alert.Source == AlertSource.Fault ? alert.EntityKey : null,
                Incident: alert.Source == AlertSource.Fault && alert.EntityKey is not null ? (int?)null : alert.Id))
                    .Select(entity => entity.MaxBy(alert => alert.Id)!).ToList();
            }).ToList();
}
