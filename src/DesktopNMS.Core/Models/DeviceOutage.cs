using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One row from /api/v0/devices/{id}/outages - a period LibreNMS recorded the
/// device as down. Both fields arrive as Unix timestamps, which the shared
/// <c>LibreNmsDateTimeConverter</c> already handles alongside the MySQL
/// datetime strings most other endpoints use.
/// </summary>
public sealed class DeviceOutage
{
    [JsonPropertyName("going_down")]
    public DateTime? GoingDown { get; set; }

    /// <summary>Null while the outage is still ongoing.</summary>
    [JsonPropertyName("up_again")]
    public DateTime? UpAgain { get; set; }
}
