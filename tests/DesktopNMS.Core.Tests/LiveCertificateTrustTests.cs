using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Trusting a certificate mid-session - the backup address's, met on failing over.</summary>
public class LiveCertificateTrustTests
{
    private const string Fingerprint = "AA:BB:CC:DD";

    private static LibreNmsConnection Connection() => new(
        new Uri("https://nms.example.net/"),
        "not-a-real-token",
        allowUntrustedCertificate: true,
        backupWebRoot: new Uri("https://192.0.2.20/"),
        trustedCertificates: new[] { "11:22:33:44" });

    [Fact]
    public void WithTrustedCertificate_keeps_everything_else()
    {
        var original = Connection();
        var updated = original.WithTrustedCertificate(Fingerprint);

        Assert.Equal(new[] { "11:22:33:44", Fingerprint }, updated.TrustedCertificates);
        Assert.Equal(original.WebRoot, updated.WebRoot);
        Assert.Equal(original.BackupWebRoot, updated.BackupWebRoot);
        Assert.Equal(original.AllowUntrustedCertificate, updated.AllowUntrustedCertificate);
        Assert.Equal(original.TimeoutSeconds, updated.TimeoutSeconds);
        Assert.Single(original.TrustedCertificates);
    }

    [Fact]
    public void The_running_transport_trusts_it_from_then_on_once()
    {
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        transport.Configure(Connection());

        transport.TrustCertificate(Fingerprint);
        transport.TrustCertificate(Fingerprint.ToLowerInvariant());

        Assert.Equal(new[] { "11:22:33:44", Fingerprint }, transport.Connection!.TrustedCertificates);
    }

    [Fact]
    public void Nothing_to_trust_when_not_signed_in()
    {
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);

        transport.TrustCertificate(Fingerprint);

        Assert.Null(transport.Connection);
    }
}
