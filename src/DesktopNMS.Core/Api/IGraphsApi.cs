using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Api;

/// <summary>
/// Device-wide graph endpoints (issue #14). LibreNMS's per-port and
/// per-sensor graph routes either 404 or 500 via the API token this app
/// authenticates with everywhere else (confirmed live, see issue #8's own
/// investigation) - only the device-wide graphs covered here are actually
/// reachable this way.
/// </summary>
public interface IGraphsApi
{
    /// <summary>GET /api/v0/devices/{id}/graphs - every device-wide graph type this device has.</summary>
    Task<IReadOnlyList<GraphType>> ListAsync(int deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/v0/devices/{id}/{graphName} - the rendered graph, as the raw
    /// SVG LibreNMS returns (confirmed live: image/svg+xml, no cookie
    /// session needed, honours "from"/"width"/"height"). Callers apply their
    /// own theming (see Infrastructure.GraphSvgTheming) before display -
    /// this returns exactly what the server sent.
    /// </summary>
    Task<string> GetSvgAsync(
        int deviceId,
        string graphName,
        GraphTimeRange range,
        int width,
        int height,
        CancellationToken cancellationToken = default);
}
