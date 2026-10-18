using System.IO;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Security;

/// <summary>Chat transcripts and model context stay encrypted for the current Windows user.</summary>
public sealed class DpapiIntelligenceChatStore : IIntelligenceChatStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DashyNMS.Intelligence.Chats.v1");
    private readonly FileIntelligenceChatStore _files = new(
        Path.Combine(AppPaths.DataDirectory, "chats"),
        plain => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser),
        encrypted => ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));

    public SavedChatCollection Load(string scope) => _files.Load(scope);
    public void Save(string scope, SavedIntelligenceChat chat, bool makeActive = true) => _files.Save(scope, chat, makeActive);
    public void Delete(string scope, System.Guid id) => _files.Delete(scope, id);
}
