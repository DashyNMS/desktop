using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Security;

/// <summary>Stores the LibreNMS API token for the current Windows user.</summary>
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

/// <summary>
/// DPAPI-backed token storage.
/// </summary>
/// <remarks>
/// Encrypted with <see cref="DataProtectionScope.CurrentUser"/>, so the file is
/// only readable by this Windows account on this machine. That is the right
/// trade-off for a desktop tool: no master password to manage, and copying the
/// file to another machine yields nothing.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtector : ITokenProtector
{
    // Ties the ciphertext to this application, so another app's DPAPI blob
    // cannot be dropped in and decrypted here.
    //
    // Deliberately left as "DesktopNMS...", the product's original name, even
    // after the DashyNMS rebrand: this is a cryptographic salt with no user
    // visibility, and changing it would fail to decrypt every already-saved
    // token (DPAPI Unprotect requires the exact same entropy used to Protect),
    // forcing every user to re-enter their API key for a purely cosmetic change.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopNMS.LibreNMS.ApiToken.v1");

    private readonly ILogger<DpapiTokenProtector> _logger;

    public DpapiTokenProtector(ILogger<DpapiTokenProtector> logger)
    {
        _logger = logger;
    }

    public bool HasStoredToken => File.Exists(AppPaths.TokenFile);

    public string? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.TokenFile))
            {
                return null;
            }

            var cipher = File.ReadAllBytes(AppPaths.TokenFile);
            if (cipher.Length == 0)
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            // Typically means the file was copied from another user or machine.
            _logger.LogWarning(ex, "The stored API token could not be decrypted; it will be discarded");
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored API token");
            return null;
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            var plain = Encoding.UTF8.GetBytes(token);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            var temp = AppPaths.TokenFile + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, AppPaths.TokenFile, overwrite: true);

            Array.Clear(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the API token");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.TokenFile))
            {
                File.Delete(AppPaths.TokenFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored API token");
        }
    }
}
