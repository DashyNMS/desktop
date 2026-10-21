using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

/// <summary>
/// macOS and Linux (#154): AES-256-GCM with one random key per user, kept in the
/// OS's own keychain - the macOS Keychain, or the Secret Service (GNOME Keyring,
/// KWallet) on Linux. The files are unreadable without that user's keychain,
/// as DPAPI's are without that Windows account.
/// </summary>
/// <remarks>
/// The key is only created when the keychain answers that there isn't one. If
/// it can't be reached - locked, declined, no Secret Service running - nothing
/// is created or deleted, so existing files aren't orphaned; secrets just
/// aren't kept until it's back.
/// </remarks>
public sealed class KeyringSecretProtector(IMasterKeyStore keys, ILogger<KeyringSecretProtector> logger) : ISecretProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly object _sync = new();
    private byte[]? _key;
    private string? _unavailableReason;

    public string? UnavailableReason
    {
        get
        {
            try
            {
                Key(create: false);
                return null;
            }
            catch (SecretStoreUnavailableException ex)
            {
                return ex.Message;
            }
        }
    }

    public byte[] Protect(byte[] plain, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var key = Key(create: true)!;
        var cipher = new byte[1 + NonceSize + TagSize + plain.Length];
        cipher[0] = Version;
        var nonce = cipher.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher.AsSpan(1 + NonceSize + TagSize), cipher.AsSpan(1 + NonceSize, TagSize), Encoding.UTF8.GetBytes(purpose));
        return cipher;
    }

    public byte[] Unprotect(byte[] cipher, string purpose)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (cipher.Length < 1 + NonceSize + TagSize || cipher[0] != Version)
        {
            throw new CryptographicException("Not a DashyNMS secret.");
        }

        // No key at all: whatever this file was encrypted with is gone.
        var key = Key(create: false) ?? throw new CryptographicException("This user's DashyNMS key isn't in the keychain.");
        var plain = new byte[cipher.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(cipher.AsSpan(1, NonceSize), cipher.AsSpan(1 + NonceSize + TagSize), cipher.AsSpan(1 + NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(purpose));
        return plain;
    }

    /// <summary>The key, read once; with <paramref name="create"/>, made and stored if the keychain has none.</summary>
    private byte[]? Key(bool create)
    {
        lock (_sync)
        {
            if (_key is not null)
            {
                return _key;
            }

            var lookup = keys.Read();
            if (lookup.Error is { } error)
            {
                if (_unavailableReason != error)
                {
                    logger.LogWarning("Secrets can't be kept: {Reason}", error);
                    _unavailableReason = error;
                }

                throw new SecretStoreUnavailableException(error);
            }

            if (lookup.Key is { Length: 32 } existing)
            {
                return _key = existing;
            }

            if (!create)
            {
                return null;
            }

            var key = RandomNumberGenerator.GetBytes(32);
            if (keys.Write(key) is { } writeError)
            {
                logger.LogWarning("Secrets can't be kept: {Reason}", writeError);
                throw new SecretStoreUnavailableException(writeError);
            }

            logger.LogInformation("Created this user's DashyNMS key in {Store}", keys.Name);
            return _key = key;
        }
    }
}
