using DesktopNMS.Core.Api;

namespace DesktopNMS.ViewModels;

/// <summary>What to say when a graph can't be shown.</summary>
internal static class GraphErrors
{
    /// <summary>
    /// A graph this account may not see (LibreNMS 26.10, DashyNMS/core#9) says
    /// so, rather than the generic API-token wording or a broken graph.
    /// </summary>
    public static string Describe(LibreNmsApiException ex)
        => ex.IsPermissionDenied && ex.ServerMessage == "No Authorization" ? ex.Message : ex.ToUserMessage();
}
