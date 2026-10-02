using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Security;

/// <summary>
/// Stores the Unimus API token for the current Windows user. A separate type
/// from <see cref="ITokenProtector"/> rather than a generalisation of it -
/// that class is tied to <see cref="AppPaths.TokenFile"/> specifically, and
/// splitting it to take an arbitrary path is a bigger change than a second,
/// independent secret warrants. Same DPAPI approach, same trade-offs (see
/// DpapiTokenProtector's own remarks, in the desktop app), different file and a
/// distinct entropy value so one secret's ciphertext can never be swapped
/// for the other's.
/// </summary>
public interface IUnimusTokenProtector
{
    bool HasStoredToken { get; }

    string? Load();

    void Save(string token);

    void Clear();
}
