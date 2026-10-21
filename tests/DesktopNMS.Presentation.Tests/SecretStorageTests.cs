using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Secrets on every platform (#154): the keychain-keyed encryption, the key stores, and the files.</summary>
public sealed class SecretStorageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashy-secrets-" + Guid.NewGuid().ToString("N"));

    public SecretStorageTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_secret_round_trips_and_is_tied_to_its_purpose()
    {
        var keys = new FakeKeyStore();
        var protector = Keyring(keys);

        var cipher = protector.Protect(Encoding.UTF8.GetBytes("token-123"), "DesktopNMS.LibreNMS.ApiToken.v1");

        Assert.Equal("token-123", Encoding.UTF8.GetString(protector.Unprotect(cipher, "DesktopNMS.LibreNMS.ApiToken.v1")));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher, "DesktopNMS.Unimus.ApiToken.v1"));
        Assert.DoesNotContain("token-123", Encoding.UTF8.GetString(cipher));
        Assert.Equal(1, keys.Writes);

        // Another run reads the same key rather than making a new one.
        Assert.Equal("token-123", Encoding.UTF8.GetString(Keyring(keys).Unprotect(cipher, "DesktopNMS.LibreNMS.ApiToken.v1")));
        Assert.Equal(1, keys.Writes);
    }

    [Fact]
    public void A_keychain_that_cant_be_reached_never_gets_a_new_key()
    {
        var keys = new FakeKeyStore { Error = "the keyring is locked" };
        var protector = Keyring(keys);

        Assert.Throws<SecretStoreUnavailableException>(() => protector.Protect([1, 2, 3], "purpose"));
        Assert.Equal(0, keys.Writes);
        Assert.Equal("the keyring is locked", protector.UnavailableReason);
    }

    [Fact]
    public void A_file_from_a_keychain_that_was_reset_is_discarded_but_one_behind_a_locked_keychain_is_kept()
    {
        var keys = new FakeKeyStore();
        var path = Path.Combine(_folder, "token.dat");
        new ProtectedSecretFile(() => path, "purpose", "API token", Keyring(keys), NullLogger.Instance).Save(Encoding.UTF8.GetBytes("secret"));

        // Locked: can't read it now, but it's still there for later.
        keys.Error = "locked";
        Assert.Null(new ProtectedSecretFile(() => path, "purpose", "API token", Keyring(keys), NullLogger.Instance).Load());
        Assert.True(File.Exists(path));

        // Reset: a different key, so it will never decrypt.
        keys.Error = null;
        keys.Key = RandomNumberGenerator.GetBytes(32);
        Assert.Null(new ProtectedSecretFile(() => path, "purpose", "API token", Keyring(keys), NullLogger.Instance).Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_saved_plain_text_is_cleared_and_never_written()
    {
        var path = Path.Combine(_folder, "graylog-password.dat");
        var plain = Encoding.UTF8.GetBytes("hunter2-password");

        new ProtectedSecretFile(() => path, "purpose", "Graylog password", Keyring(new FakeKeyStore()), NullLogger.Instance).Save(plain);

        Assert.All(plain, b => Assert.Equal(0, b));
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    [Fact]
    public void On_Windows_DPAPI_still_opens_what_the_WPF_app_saved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // What DpapiTokenProtector wrote, before #154.
        var saved = ProtectedData.Protect(Encoding.UTF8.GetBytes("old-token"), Encoding.UTF8.GetBytes("DesktopNMS.LibreNMS.ApiToken.v1"), DataProtectionScope.CurrentUser);

        Assert.Equal("old-token", Encoding.UTF8.GetString(new DpapiSecretProtector().Unprotect(saved, "DesktopNMS.LibreNMS.ApiToken.v1")));
    }

    [Theory]
    [InlineData(0, "", null, true)]
    [InlineData(44, "", null, false)]
    [InlineData(51, "", "couldn't be read (exit 51)", false)]
    public void The_macOS_Keychain_tells_no_key_from_no_answer(int exit, string error, string? expectedError, bool found)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var runner = new FakeRunner(new CommandResult(true, exit, exit == 0 ? key + "\n" : string.Empty, error));

        var lookup = new MacKeychainKeyStore(runner).Read();

        Assert.Equal(found, lookup.Key is not null);
        if (expectedError is null) Assert.Null(lookup.Error);
        else Assert.Contains(expectedError, lookup.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_macOS_key_goes_in_on_stdin_not_the_command_line()
    {
        var runner = new FakeRunner(new CommandResult(true, 0, string.Empty, string.Empty));
        var key = RandomNumberGenerator.GetBytes(32);

        Assert.Null(new MacKeychainKeyStore(runner).Write(key));

        Assert.Equal(["-i"], runner.Arguments);
        Assert.Contains(Convert.ToBase64String(key), runner.Input, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 1, "", null, false)]
    [InlineData(true, 1, "Cannot autolaunch D-Bus without X11 $DISPLAY", "Cannot autolaunch D-Bus", false)]
    [InlineData(false, -1, "", "secret-tool isn't installed", false)]
    public void The_Secret_Service_tells_no_key_from_no_answer(bool started, int exit, string error, string? expectedError, bool found)
    {
        var lookup = new SecretServiceKeyStore(new FakeRunner(new CommandResult(started, exit, string.Empty, error))).Read();

        Assert.Equal(found, lookup.Key is not null);
        if (expectedError is null) Assert.Null(lookup.Error);
        else Assert.Contains(expectedError, lookup.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Secret_Service_key_goes_in_on_stdin_and_is_read_back()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var runner = new FakeRunner(new CommandResult(true, 0, string.Empty, string.Empty));

        Assert.Null(new SecretServiceKeyStore(runner).Write(key));
        Assert.Equal(Convert.ToBase64String(key), runner.Input);
        Assert.DoesNotContain(runner.Arguments!, a => a.Contains(Convert.ToBase64String(key), StringComparison.Ordinal));

        var read = new SecretServiceKeyStore(new FakeRunner(new CommandResult(true, 0, Convert.ToBase64String(key), string.Empty))).Read();
        Assert.Equal(key, read.Key);
    }

    [Fact]
    public void A_failure_message_never_carries_the_tools_output()
    {
        var result = new CommandResult(true, 2, "c2VjcmV0LWtleQ==", "something went wrong\nmore");

        Assert.Equal("exit 2: something went wrong", result.Describe());
    }

    private static KeyringSecretProtector Keyring(FakeKeyStore keys) => new(keys, NullLogger<KeyringSecretProtector>.Instance);

    private sealed class FakeKeyStore : IMasterKeyStore
    {
        public byte[]? Key { get; set; }
        public string? Error { get; set; }
        public int Writes { get; private set; }
        public string Name => "the fake keychain";

        public MasterKeyLookup Read() => new(Error is null ? Key : null, Error);

        public string? Write(byte[] key)
        {
            Writes++;
            Key = key;
            return null;
        }
    }

    private sealed class FakeRunner(CommandResult result) : ICommandRunner
    {
        public string[]? Arguments { get; private set; }
        public string? Input { get; private set; }

        public CommandResult Run(string file, string[] arguments, string? input = null)
        {
            Arguments = arguments;
            Input = input;
            return result;
        }
    }
}
