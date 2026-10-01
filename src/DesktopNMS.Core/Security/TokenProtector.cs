using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Security;

/// <summary>Stores the LibreNMS API token for the current user. Each app supplies its own platform's secure storage - DPAPI on Windows (see DpapiTokenProtector in the desktop app).</summary>
public interface ITokenProtector
{
    /// <summary>True when a token has been saved on this machine.</summary>
    bool HasStoredToken { get; }

    /// <summary>Returns the stored token, or null when there is none or it cannot be decrypted.</summary>
    string? Load();

    /// <summary>Encrypts and stores the token.</summary>
    void Save(string token);

    /// <summary>Deletes the stored token.</summary>
    void Clear();
}
