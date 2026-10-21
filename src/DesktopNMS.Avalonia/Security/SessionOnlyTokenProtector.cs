using DesktopNMS.Core.Security;

namespace DesktopNMS.Security;

/// <summary>
/// Keeps the API token for this run only (#153). Saving secrets on each OS -
/// Windows DPAPI, the macOS Keychain, the Linux secret service - is #154; until
/// then the scaffold asks for the token every time and writes it nowhere.
/// </summary>
public sealed class SessionOnlyTokenProtector : ITokenProtector
{
    private string? _token;

    public bool HasStoredToken => _token is not null;

    public void Save(string token) => _token = token;

    public string? Load() => _token;

    public void Clear() => _token = null;
}
