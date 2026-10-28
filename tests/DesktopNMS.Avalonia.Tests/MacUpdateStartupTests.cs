using System.Text.Json;
using DesktopNMS.Services;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class MacUpdateStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashynms-update-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly string _transaction;

    public MacUpdateStartupTests()
    {
        _app = Path.Combine(_root, "DashyNMS.app");
        _transaction = Path.Combine(_root, ".DashyNMS-update-" + Guid.NewGuid());
        Directory.CreateDirectory(_app);
        Directory.CreateDirectory(_transaction);
        File.WriteAllText(Path.Combine(_transaction, "transaction.json"), JsonSerializer.Serialize(new
        {
            app = _app, version = "2.0.0-preview.10", token = "synthetic-token",
        }));
    }

    [Fact]
    public void Startup_confirms_the_exact_installed_preview_and_process()
    {
        Assert.True(MacUpdateInstallation.TryConfirmStartup(_app, _transaction, "synthetic-token", "2.0.0-preview.10", 123));
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(_transaction, "startup.json")));
        Assert.Equal("2.0.0-preview.10", receipt.RootElement.GetProperty("version").GetString());
        Assert.Equal(123, receipt.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("synthetic-token", receipt.RootElement.GetProperty("token").GetString());
    }

    [Theory]
    [InlineData("2.0.0-preview.9", "synthetic-token")]
    [InlineData("2.0.0", "synthetic-token")]
    [InlineData("2.0.0-preview.10", "wrong-token")]
    public void A_wrong_release_or_token_cannot_confirm_startup(string version, string token)
    {
        Assert.False(MacUpdateInstallation.TryConfirmStartup(_app, _transaction, token, version, 123));
        Assert.False(File.Exists(Path.Combine(_transaction, "startup.json")));
    }

    [Fact]
    public void A_receipt_cannot_be_written_for_another_installation_or_outside_its_transaction()
    {
        Assert.False(MacUpdateInstallation.TryConfirmStartup(Path.Combine(_root, "Other.app"), _transaction, "synthetic-token", "2.0.0-preview.10", 123));
        Assert.False(MacUpdateInstallation.TryConfirmStartup(_app, _root, "synthetic-token", "2.0.0-preview.10", 123));
        Assert.False(File.Exists(Path.Combine(_transaction, "startup.json")));
        Assert.False(File.Exists(Path.Combine(_root, "startup.json")));
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("{}")]
    [InlineData("{\"token\":123}")]
    public void Damaged_transaction_metadata_cannot_crash_startup_or_confirm_the_update(string contents)
    {
        File.WriteAllText(Path.Combine(_transaction, "transaction.json"), contents);
        Assert.False(MacUpdateInstallation.TryConfirmStartup(_app, _transaction, "synthetic-token", "2.0.0-preview.10", 123));
        Assert.False(File.Exists(Path.Combine(_transaction, "startup.json")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
