using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Api;

/// <summary>
/// A LibreNMS write permission, one per policy check its API routes make
/// (routes/api.php: "can:create,App\Models\AlertRule" and so on). A token
/// carries its user's permissions, and a read-only or limited user's token
/// is refused these with a 403 (#51).
/// </summary>
public enum ApiPermission
{
    AcknowledgeAlerts,
    CreateRules,
    EditRules,
    DeleteRules,

    /// <summary>Creating and editing alert templates - both go through POST alert_templates.</summary>
    ChangeTemplates,

    AddDevices,

    /// <summary>Editing, renaming and scheduling maintenance for a device - all "can:update" on the device.</summary>
    EditDevices,

    DeleteDevices,
    CreateGroups,

    /// <summary>Editing a group, changing its devices, and scheduling maintenance for it.</summary>
    EditGroups,

    DeleteGroups,
    CreateLocations,
    EditLocations,
    DeleteLocations,
}

/// <summary>
/// What the API token turned out not to be allowed to do, learned as it goes
/// (#51). LibreNMS has no call that lists a token's permissions, and the
/// only way to test a write is to make it - so nothing is probed: the first
/// time a write comes back 403, its <see cref="ApiPermission"/> is remembered
/// here until the connection changes, and the app turns the matching buttons
/// off with the reason.
/// </summary>
public sealed class ApiPermissions
{
    private static readonly (HttpMethod Method, Regex Route, ApiPermission Permission)[] Routes =
    {
        (HttpMethod.Put, Route("alerts/[^/]+"), ApiPermission.AcknowledgeAlerts),
        (HttpMethod.Put, Route("alerts/unmute/[^/]+"), ApiPermission.AcknowledgeAlerts),

        (HttpMethod.Post, Route("rules"), ApiPermission.CreateRules),
        (HttpMethod.Put, Route("rules"), ApiPermission.EditRules),
        (HttpMethod.Delete, Route("rules/[^/]+"), ApiPermission.DeleteRules),

        (HttpMethod.Post, Route("alert_templates"), ApiPermission.ChangeTemplates),
        (HttpMethod.Put, Route("alert_templates"), ApiPermission.ChangeTemplates),

        (HttpMethod.Post, Route("devices"), ApiPermission.AddDevices),
        (HttpMethod.Patch, Route("devices/[^/]+"), ApiPermission.EditDevices),
        (HttpMethod.Patch, Route("devices/[^/]+/rename/[^/]+"), ApiPermission.EditDevices),
        (HttpMethod.Post, Route("devices/[^/]+/maintenance"), ApiPermission.EditDevices),
        (HttpMethod.Delete, Route("devices/[^/]+"), ApiPermission.DeleteDevices),

        (HttpMethod.Post, Route("devicegroups"), ApiPermission.CreateGroups),
        (HttpMethod.Patch, Route("devicegroups/[^/]+"), ApiPermission.EditGroups),
        (HttpMethod.Post, Route("devicegroups/[^/]+/(devices|maintenance)"), ApiPermission.EditGroups),
        (HttpMethod.Delete, Route("devicegroups/[^/]+/devices"), ApiPermission.EditGroups),
        (HttpMethod.Delete, Route("devicegroups/[^/]+"), ApiPermission.DeleteGroups),

        (HttpMethod.Post, Route("locations"), ApiPermission.CreateLocations),
        (HttpMethod.Patch, Route("locations/[^/]+"), ApiPermission.EditLocations),
        (HttpMethod.Post, Route("locations/[^/]+/maintenance"), ApiPermission.EditLocations),
        (HttpMethod.Delete, Route("locations/[^/]+"), ApiPermission.DeleteLocations),
    };

    private readonly object _sync = new();
    private readonly HashSet<ApiPermission> _refused = new();

    /// <summary>Raised, on whichever thread made the request, when a permission is first refused or the list is forgotten.</summary>
    public event EventHandler? Changed;

    public bool IsRefused(ApiPermission permission)
    {
        lock (_sync)
        {
            return _refused.Contains(permission);
        }
    }

    /// <summary>
    /// Remembers the permission behind a request that came back 403.
    /// Anything else - another status, a read, a route that isn't a write
    /// listed here - is ignored. True when this is news.
    /// </summary>
    public bool Learn(HttpMethod method, string relativeUrl, LibreNmsApiException exception)
    {
        if (!exception.IsPermissionDenied || For(method, relativeUrl) is not { } permission)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_refused.Add(permission))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Starts over - a new connection may be a different token, with different permissions.</summary>
    public void Forget()
    {
        lock (_sync)
        {
            if (_refused.Count == 0)
            {
                return;
            }

            _refused.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The permission a write request needs, or null for a read or a route not listed.</summary>
    public static ApiPermission? For(HttpMethod method, string relativeUrl)
    {
        var path = relativeUrl.Split('?', 2)[0].Trim('/');

        foreach (var (routeMethod, route, permission) in Routes)
        {
            if (routeMethod == method && route.IsMatch(path))
            {
                return permission;
            }
        }

        return null;
    }

    /// <summary>Why a button is off: "Your API token isn't allowed to delete devices in LibreNMS."</summary>
    public static string Describe(ApiPermission permission) =>
        $"Your API token isn't allowed to {Verb(permission)} in LibreNMS.";

    private static string Verb(ApiPermission permission) => permission switch
    {
        ApiPermission.AcknowledgeAlerts => "acknowledge alerts",
        ApiPermission.CreateRules => "create alert rules",
        ApiPermission.EditRules => "edit alert rules",
        ApiPermission.DeleteRules => "delete alert rules",
        ApiPermission.ChangeTemplates => "create or edit alert templates",
        ApiPermission.AddDevices => "add devices",
        ApiPermission.EditDevices => "edit devices",
        ApiPermission.DeleteDevices => "delete devices",
        ApiPermission.CreateGroups => "create device groups",
        ApiPermission.EditGroups => "edit device groups",
        ApiPermission.DeleteGroups => "delete device groups",
        ApiPermission.CreateLocations => "create locations",
        ApiPermission.EditLocations => "edit locations",
        ApiPermission.DeleteLocations => "delete locations",
        _ => "do this",
    };

    private static Regex Route(string pattern) =>
        new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
