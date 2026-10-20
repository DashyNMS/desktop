namespace DesktopNMS.Services;

/// <summary>
/// The front end's map tile source (#249). View models only pass it through
/// to their map canvas, which knows the real type (the WPF app's
/// IMapTileService, which returns WPF images).
/// </summary>
public interface IMapTiles
{
}
