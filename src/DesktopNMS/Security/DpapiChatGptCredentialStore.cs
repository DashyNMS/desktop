using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

/// <summary>
/// The ChatGPT sign-in, encrypted with DPAPI for the current Windows user, in
/// the data folder - so signing out of DashyNMS removes it with everything else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiChatGptCredentialStore : IChatGptCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopNMS.ChatGpt.Credentials.v1");

    private readonly ILogger<DpapiChatGptCredentialStore> _logger;

    public DpapiChatGptCredentialStore(ILogger<DpapiChatGptCredentialStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "chatgpt.dat");

    public ChatGptCredentials? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<ChatGptCredentials>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning("The stored ChatGPT sign-in couldn't be read; it will be discarded");
            Clear();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the stored ChatGPT sign-in");
            return null;
        }
    }

    public void Save(ChatGptCredentials credentials)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Array.Clear(plain);

            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the ChatGPT sign-in");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored ChatGPT sign-in");
        }
    }
}
