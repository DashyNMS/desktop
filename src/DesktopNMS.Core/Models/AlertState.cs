namespace DesktopNMS.Core.Models;

/// <summary>
/// Value of the alerts.state column in LibreNMS.
/// </summary>
public enum AlertState
{
    /// <summary>The condition has cleared. LibreNMS calls this "recovered".</summary>
    Recovered = 0,

    /// <summary>Firing and not acknowledged.</summary>
    Active = 1,

    /// <summary>Acknowledged by an operator.</summary>
    Acknowledged = 2,

    /// <summary>Condition worsened (seen in alert history rather than the alerts table).</summary>
    Worse = 3,

    /// <summary>Condition improved but has not cleared.</summary>
    Better = 4,
}

public static class AlertStateExtensions
{
    public static AlertState FromValue(int value) => value switch
    {
        0 => AlertState.Recovered,
        1 => AlertState.Active,
        2 => AlertState.Acknowledged,
        3 => AlertState.Worse,
        4 => AlertState.Better,
        _ => AlertState.Active,
    };

    public static string ToDisplayString(this AlertState state) => state switch
    {
        AlertState.Recovered => "Recovered",
        AlertState.Active => "Active",
        AlertState.Acknowledged => "Acknowledged",
        AlertState.Worse => "Worsened",
        AlertState.Better => "Improved",
        _ => "Unknown",
    };
}
