using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>
/// Compares the alert list from one poll against the last one, and reports what
/// changed. Pure and side-effect free so it can be unit tested directly.
/// </summary>
public static class AlertChangeDetector
{
    /// <summary>
    /// Produces the changes between a previous snapshot and the alerts just fetched.
    /// </summary>
    /// <param name="previousStates">Alert id to the alerts.state value seen last time.</param>
    /// <param name="current">Alerts returned by the latest poll.</param>
    /// <returns>Changes worth acting on, in the order the alerts were returned.</returns>
    public static IReadOnlyList<AlertChange> Detect(
        IReadOnlyDictionary<int, int> previousStates,
        IReadOnlyList<Alert> current)
    {
        ArgumentNullException.ThrowIfNull(previousStates);
        ArgumentNullException.ThrowIfNull(current);

        var changes = new List<AlertChange>();

        foreach (var alert in current)
        {
            if (!previousStates.TryGetValue(alert.Id, out var previousValue))
            {
                // Not seen before. A recovered alert appearing for the first time
                // is history, not news, so it is not reported.
                if (alert.State != AlertState.Recovered)
                {
                    changes.Add(new AlertChange(alert, AlertChangeKind.New, previousState: null));
                }

                continue;
            }

            if (previousValue == alert.StateValue)
            {
                continue;
            }

            var previousState = AlertStateExtensions.FromValue(previousValue);

            var kind = alert.State switch
            {
                AlertState.Recovered => AlertChangeKind.Recovered,
                AlertState.Acknowledged => AlertChangeKind.Acknowledged,
                AlertState.Active when previousState == AlertState.Recovered => AlertChangeKind.Reopened,
                AlertState.Active when previousState == AlertState.Acknowledged => AlertChangeKind.Unacknowledged,
                _ => AlertChangeKind.New,
            };

            changes.Add(new AlertChange(alert, kind, previousState));
        }

        return changes;
    }

    /// <summary>
    /// Builds the snapshot to carry into the next poll. Alerts that disappeared
    /// from the server (rule deleted, alert purged) drop out naturally.
    /// </summary>
    public static Dictionary<int, int> Snapshot(IReadOnlyList<Alert> current)
    {
        var snapshot = new Dictionary<int, int>(current.Count);

        foreach (var alert in current)
        {
            snapshot[alert.Id] = alert.StateValue;
        }

        return snapshot;
    }
}
