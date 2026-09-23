using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Core.Security;

/// <summary>
/// Stores the Unimus API token for the current Windows user. A separate type
/// from <see cref="ITokenProtector"/> rather than a generalisation of it -
/// that class is tied to <see cref="AppPaths.TokenFile"/> specifically, and
/// splitting it to take an arbitrary path is a bigger change than a second,
/// independent secret warrants. Same DPAPI approach, same trade-offs (see
/// <see cref="DpapiTokenProtector"/>'s own remarks), different file and a
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

[SupportedOSPlatform("windows")]
public sealed class DpapiUnimusTokenProtector : IUnimusTokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopNMS.Unimus.ApiToken.v1");

    private readonly ILogger<DpapiUnimusTokenProtector> _logger;

    public DpapiUnimusTokenProtector(ILogger<DpapiUnimusTokenProtector> logger)
    {
        _logger = logger;
    }

    public bool HasStoredToken => File.Exists(AppPaths.UnimusTokenFile);

    public string? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.UnimusTokenFile))
            {
                return null;
            }

            var cipher = File.ReadAllBytes(AppPaths.UnimusTokenFile);
            if (cipher.Length == 0)
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "The stored Unimus API token could not be decrypted; it will be discarded");
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored Unimus API token");
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

            var temp = AppPaths.UnimusTokenFile + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, AppPaths.UnimusTokenFile, overwrite: true);

            Array.Clear(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the Unimus API token");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.UnimusTokenFile))
            {
                File.Delete(AppPaths.UnimusTokenFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored Unimus API token");
        }
    }
}
