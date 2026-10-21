using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

/// <summary>
/// Encrypts secrets for the current user on this computer (#154): the API
/// tokens, the Graylog password, the ChatGPT sign-in and saved chats. One
/// per platform - see <see cref="SecretProtectors.ForThisPlatform"/>.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Null when secrets can be kept; otherwise why not, for the log and Settings.</summary>
    string? UnavailableReason { get; }

    /// <param name="purpose">What the secret is ("DesktopNMS.LibreNMS.ApiToken.v1"): one secret can't be passed off as another.</param>
    /// <exception cref="SecretStoreUnavailableException">Secrets can't be kept on this computer just now.</exception>
    byte[] Protect(byte[] plain, string purpose);

    /// <exception cref="CryptographicException">Not this user's, not for this purpose, or damaged - it will never decrypt.</exception>
    /// <exception cref="SecretStoreUnavailableException">The key couldn't be reached just now; the secret may still be good.</exception>
    byte[] Unprotect(byte[] cipher, string purpose);
}

/// <summary>The keychain or secret service couldn't be reached - not that the secret is bad, so it's kept for next time.</summary>
public sealed class SecretStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Picks the secret protector for the OS DashyNMS is running on.</summary>
public static class SecretProtectors
{
    /// <summary>DPAPI on Windows; a key in the Keychain on macOS, or in the Secret Service on Linux.</summary>
    public static ISecretProtector ForThisPlatform(ILoggerFactory loggers)
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSecretProtector();
        }

        IMasterKeyStore keys = OperatingSystem.IsMacOS()
            ? new MacKeychainKeyStore(new ProcessCommandRunner())
            : new SecretServiceKeyStore(new ProcessCommandRunner());
        return new KeyringSecretProtector(keys, loggers.CreateLogger<KeyringSecretProtector>());
    }
}

/// <summary>
/// Windows: DPAPI for the current user, with the purpose as its entropy -
/// exactly what the WPF app always wrote, so saved tokens and chats still open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string? UnavailableReason => null;

    public byte[] Protect(byte[] plain, string purpose)
        => ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] cipher, string purpose)
        => ProtectedData.Unprotect(cipher, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
}
