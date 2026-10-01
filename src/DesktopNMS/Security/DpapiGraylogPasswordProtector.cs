using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiGraylogPasswordProtector : IGraylogPasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopNMS.Graylog.Password.v1");

    private readonly ILogger<DpapiGraylogPasswordProtector> _logger;

    public DpapiGraylogPasswordProtector(ILogger<DpapiGraylogPasswordProtector> logger)
    {
        _logger = logger;
    }

    public bool HasStoredPassword => File.Exists(AppPaths.GraylogPasswordFile);

    public string? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.GraylogPasswordFile))
            {
                return null;
            }

            var cipher = File.ReadAllBytes(AppPaths.GraylogPasswordFile);
            if (cipher.Length == 0)
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "The stored Graylog password could not be decrypted; it will be discarded");
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored Graylog password");
            return null;
        }
    }

    public void Save(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        try
        {
            var plain = Encoding.UTF8.GetBytes(password);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            var temp = AppPaths.GraylogPasswordFile + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, AppPaths.GraylogPasswordFile, overwrite: true);

            Array.Clear(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the Graylog password");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.GraylogPasswordFile))
            {
                File.Delete(AppPaths.GraylogPasswordFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored Graylog password");
        }
    }
}
