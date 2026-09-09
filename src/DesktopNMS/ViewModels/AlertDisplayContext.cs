using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Services;

namespace DesktopNMS.ViewModels;

/// <summary>
/// The ambient settings a row needs to render itself. Bundled so adding another
/// display option does not mean threading another parameter through every row.
/// </summary>
public sealed class AlertDisplayContext
{
    public AlertDisplayContext(
        bool serverTimestampsAreUtc,
        LibreNmsConnection? connection,
        DeviceNameStyle deviceNameStyle,
        IDeviceCache? devices)
    {
        ServerTimestampsAreUtc = serverTimestampsAreUtc;
        Connection = connection;
        DeviceNameStyle = deviceNameStyle;
        Devices = devices;
    }

    public bool ServerTimestampsAreUtc { get; }

    public LibreNmsConnection? Connection { get; }

    public DeviceNameStyle DeviceNameStyle { get; }

    public IDeviceCache? Devices { get; }

    public Device? DeviceFor(int deviceId) => Devices?.Get(deviceId);

    /// <summary>Builds the context from current settings and session state.</summary>
    public static AlertDisplayContext Create(AppSettings settings, LibreNmsConnection? connection, IDeviceCache? devices)
        => new(settings.ServerTimestampsAreUtc, connection, settings.DeviceNameStyle, devices);
}
