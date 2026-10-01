using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Security;

/// <summary>
/// Stores the Graylog API password for the current Windows user - the same
/// shape as <see cref="IUnimusTokenProtector"/> (see its remarks for why each
/// secret gets its own type), with its own file and its own entropy value so
/// no secret's ciphertext can be swapped for another's.
/// </summary>
public interface IGraylogPasswordProtector
{
    bool HasStoredPassword { get; }

    string? Load();

    void Save(string password);

    void Clear();
}
