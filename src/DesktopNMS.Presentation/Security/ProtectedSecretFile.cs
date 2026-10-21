using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

/// <summary>
/// One secret in one file, encrypted with <see cref="ISecretProtector"/> (#154) -
/// what each DPAPI store in the WPF app used to do for itself.
/// </summary>
/// <remarks>
/// A file that can never be decrypted (another user's or computer's, or
/// damaged) is discarded so the person is asked again. One that can't be read
/// because the keychain is out of reach is kept for when it's back.
/// </remarks>
public sealed class ProtectedSecretFile(Func<string> path, string purpose, string what, ISecretProtector protector, ILogger logger)
{
    public bool Exists => File.Exists(path());

    /// <summary>The secret, or null when there's none or it can't be read.</summary>
    public byte[]? Load()
    {
        var file = path();
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var cipher = File.ReadAllBytes(file);
            return cipher.Length == 0 ? null : protector.Unprotect(cipher, purpose);
        }
        catch (CryptographicException ex)
        {
            // Typically a file copied from another user or computer.
            logger.LogWarning(ex, "The stored {What} could not be decrypted; it will be discarded", what);
            Clear();
            return null;
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning("The stored {What} can't be read just now: {Reason}", what, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the stored {What}", what);
            return null;
        }
    }

    /// <summary>Encrypts and stores the secret, then clears <paramref name="plain"/>.</summary>
    public void Save(byte[] plain)
    {
        var file = path();
        try
        {
            var cipher = protector.Protect(plain, purpose);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, file, overwrite: true);
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogWarning("The {What} wasn't stored: {Reason}", what, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store the {What}", what);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public void Clear()
    {
        try
        {
            var file = path();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the stored {What}", what);
        }
    }
}
