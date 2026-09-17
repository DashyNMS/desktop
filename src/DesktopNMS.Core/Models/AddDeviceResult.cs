namespace DesktopNMS.Core.Models;

/// <summary>
/// Result of POST /api/v0/devices: the server's own confirmation message
/// (e.g. "Device localhost (57) has been added successfully"), plus the new
/// device's id when the response echoed one back.
/// </summary>
public sealed record AddDeviceResult(string Message, int? DeviceId);
