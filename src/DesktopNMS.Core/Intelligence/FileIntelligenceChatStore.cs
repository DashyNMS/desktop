using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopNMS.Core.Intelligence;

/// <summary>Atomic, per-chat files. The platform supplies encryption; scopes
/// separate LibreNMS servers and ChatGPT accounts without exposing their names.</summary>
public sealed class FileIntelligenceChatStore(string root, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect) : IIntelligenceChatStore
{
    private readonly object _sync = new();

    public SavedChatCollection Load(string scope)
    {
        lock (_sync)
        {
            var directory = DirectoryFor(scope);
            if (!Directory.Exists(directory)) return new(null, Array.Empty<SavedIntelligenceChat>());
            var chats = Directory.EnumerateFiles(directory, "*.chat").Select(file =>
            {
                var data = Read(file);
                try
                {
                    var chat = JsonSerializer.Deserialize<SavedIntelligenceChat>(data) ?? throw new JsonException("Empty saved chat.");
                    if (Path.GetFileNameWithoutExtension(file) != chat.Id.ToString("N")) throw new JsonException("Saved chat identity mismatch.");
                    return chat;
                }
                finally { Array.Clear(data); }
            }).OrderByDescending(chat => chat.IsPinned).ThenByDescending(chat => chat.UpdatedAt).ToList();
            Guid? active = null;
            var statePath = Path.Combine(directory, "active.dat");
            if (File.Exists(statePath))
            {
                var data = Read(statePath);
                try { active = JsonSerializer.Deserialize<Guid>(data); }
                finally { Array.Clear(data); }
            }
            return new(active, chats);
        }
    }

    public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true)
    {
        lock (_sync)
        {
            var directory = DirectoryFor(scope);
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, chat.Id.ToString("N") + ".chat"), JsonSerializer.SerializeToUtf8Bytes(chat));
            if (makeActive) Write(Path.Combine(directory, "active.dat"), JsonSerializer.SerializeToUtf8Bytes(chat.Id));
        }
    }

    public void Delete(string scope, Guid id)
    {
        lock (_sync) File.Delete(Path.Combine(DirectoryFor(scope), id.ToString("N") + ".chat"));
    }

    private string DirectoryFor(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))));
    }

    private byte[] Read(string path) => unprotect(File.ReadAllBytes(path));

    private void Write(string path, byte[] plain)
    {
        try
        {
            var encrypted = protect(plain);
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally { Array.Clear(plain); }
    }
}
