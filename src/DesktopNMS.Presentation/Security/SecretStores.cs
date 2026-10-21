using System;
using System.IO;
using System.Text;
using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Security;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.Security;

// The secrets DashyNMS keeps, on every platform (#154). File names and purposes
// are what the WPF app's DPAPI stores always used - "DesktopNMS..." from before
// the rename included - so on Windows everything already saved still opens.

/// <summary>The LibreNMS API token.</summary>
public sealed class TokenProtector(ISecretProtector protector, ILogger<TokenProtector> logger) : ITokenProtector
{
    private readonly ProtectedSecretFile _file = new(() => AppPaths.TokenFile, "DesktopNMS.LibreNMS.ApiToken.v1", "API token", protector, logger);

    public bool HasStoredToken => _file.Exists;

    public string? Load() => _file.Load() is { } plain ? Encoding.UTF8.GetString(plain) : null;

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _file.Save(Encoding.UTF8.GetBytes(token));
    }

    public void Clear() => _file.Clear();
}

/// <summary>The Unimus API token.</summary>
public sealed class UnimusTokenProtector(ISecretProtector protector, ILogger<UnimusTokenProtector> logger) : IUnimusTokenProtector
{
    private readonly ProtectedSecretFile _file = new(() => AppPaths.UnimusTokenFile, "DesktopNMS.Unimus.ApiToken.v1", "Unimus token", protector, logger);

    public bool HasStoredToken => _file.Exists;

    public string? Load() => _file.Load() is { } plain ? Encoding.UTF8.GetString(plain) : null;

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _file.Save(Encoding.UTF8.GetBytes(token));
    }

    public void Clear() => _file.Clear();
}

/// <summary>The Graylog password, or access token.</summary>
public sealed class GraylogPasswordProtector(ISecretProtector protector, ILogger<GraylogPasswordProtector> logger) : IGraylogPasswordProtector
{
    private readonly ProtectedSecretFile _file = new(() => AppPaths.GraylogPasswordFile, "DesktopNMS.Graylog.Password.v1", "Graylog password", protector, logger);

    public bool HasStoredPassword => _file.Exists;

    public string? Load() => _file.Load() is { } plain ? Encoding.UTF8.GetString(plain) : null;

    public void Save(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        _file.Save(Encoding.UTF8.GetBytes(password));
    }

    public void Clear() => _file.Clear();
}

/// <summary>The ChatGPT sign-in, in the data folder - so signing out of DashyNMS removes it with everything else.</summary>
public sealed class ChatGptCredentialStore(ISecretProtector protector, ILogger<ChatGptCredentialStore> logger) : IChatGptCredentialStore
{
    private readonly ProtectedSecretFile _file = new(() => Path.Combine(AppPaths.DataDirectory, "chatgpt.dat"), "DesktopNMS.ChatGpt.Credentials.v1", "ChatGPT sign-in", protector, logger);
    private readonly ILogger _logger = logger;

    public ChatGptCredentials? Load()
    {
        if (_file.Load() is not { } plain)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChatGptCredentials>(plain);
        }
        catch (JsonException)
        {
            _logger.LogWarning("The stored ChatGPT sign-in couldn't be read; it will be discarded");
            _file.Clear();
            return null;
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public void Save(ChatGptCredentials credentials) => _file.Save(JsonSerializer.SerializeToUtf8Bytes(credentials));

    public void Clear() => _file.Clear();
}

/// <summary>Saved Intelligence chats - transcripts and model context - encrypted like every other secret.</summary>
public sealed class IntelligenceChatStore(ISecretProtector protector) : IIntelligenceChatStore
{
    private const string Purpose = "DashyNMS.Intelligence.Chats.v1";

    // The files' own store tells damaged files (CryptographicException) from
    // ones it can't reach yet (IOException) - a keychain out of reach is the latter.
    private readonly FileIntelligenceChatStore _files = new(
        Path.Combine(AppPaths.DataDirectory, "chats"),
        plain => Guard(() => protector.Protect(plain, Purpose)),
        encrypted => Guard(() => protector.Unprotect(encrypted, Purpose)));

    public SavedChatCollection Load(string scope) => _files.Load(scope);

    public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true) => _files.Save(scope, chat, makeActive);

    public void Delete(string scope, Guid id) => _files.Delete(scope, id);

    private static byte[] Guard(Func<byte[]> work)
    {
        try
        {
            return work();
        }
        catch (SecretStoreUnavailableException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
