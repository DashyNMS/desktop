using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Alerting;

/// <summary>What happened to an alert between two polls.</summary>
public enum AlertChangeKind
{
    /// <summary>An alert id that was not present in the previous snapshot.</summary>
    New,

    /// <summary>Previously recovered, now firing again.</summary>
    Reopened,

    /// <summary>Moved to state 2 (acknowledged).</summary>
    Acknowledged,

    /// <summary>Was acknowledged, has gone back to state 1.</summary>
    Unacknowledged,

    /// <summary>Moved to state 0 (recovered).</summary>
    Recovered,
}

/// <summary>A single difference between the previous and current alert snapshots.</summary>
public sealed class AlertChange
{
    public AlertChange(Alert alert, AlertChangeKind kind, AlertState? previousState)
    {
        Alert = alert;
        Kind = kind;
        PreviousState = previousState;
    }

    public Alert Alert { get; }

    public AlertChangeKind Kind { get; }

    /// <summary>Null when the alert was not in the previous snapshot.</summary>
    public AlertState? PreviousState { get; }

    /// <summary>True for the kinds that represent a problem starting or resuming.</summary>
    public bool IsProblem => Kind is AlertChangeKind.New or AlertChangeKind.Reopened or AlertChangeKind.Unacknowledged;

    public override string ToString() => $"{Kind}: {Alert}";
}
