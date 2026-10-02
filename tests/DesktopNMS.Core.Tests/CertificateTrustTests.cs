using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Security;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class CertificateTrustTests
{
    private const string Pinned = "AA:BB:CC:DD";

    [Fact]
    public void A_certificate_that_passes_the_normal_checks_is_always_accepted()
    {
        Assert.True(CertificateTrust.Accepts(SslPolicyErrors.None, "11:22", allowUntrusted: false, trustedFingerprints: null));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void With_the_option_off_nothing_else_is_accepted_even_if_pinned(SslPolicyErrors errors)
    {
        Assert.False(CertificateTrust.Accepts(errors, Pinned, allowUntrusted: false, new[] { Pinned }));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void With_the_option_on_an_unpinned_certificate_is_refused_not_accepted(SslPolicyErrors errors)
    {
        // The old behaviour (#189) accepted all of these - including one for a different host.
        Assert.False(CertificateTrust.Accepts(errors, "11:22:33", allowUntrusted: true, new[] { Pinned }));
        Assert.False(CertificateTrust.Accepts(errors, "11:22:33", allowUntrusted: true, Array.Empty<string>()));
    }

    [Fact]
    public void With_the_option_on_the_exact_pinned_certificate_is_accepted()
    {
        Assert.True(CertificateTrust.Accepts(SslPolicyErrors.RemoteCertificateChainErrors, Pinned, allowUntrusted: true, new[] { Pinned }));
    }

    [Fact]
    public void No_certificate_at_all_is_never_accepted()
    {
        Assert.False(CertificateTrust.Accepts(SslPolicyErrors.RemoteCertificateNotAvailable, null, allowUntrusted: true, new[] { Pinned }));
    }

    [Theory]
    [InlineData("aa:bb:cc:dd")]
    [InlineData("AABBCCDD")]
    [InlineData("aa bb cc dd")]
    public void Fingerprints_match_ignoring_case_and_separators(string other)
    {
        Assert.True(CertificateTrust.SameFingerprint(Pinned, other));
    }

    [Fact]
    public void A_real_certificate_gets_a_colon_separated_SHA256_fingerprint()
    {
        using var certificate = SelfSigned("nms.example.net");

        var fingerprint = CertificateTrust.Fingerprint(certificate);

        Assert.Equal(32, fingerprint.Split(':').Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), fingerprint.Replace(":", string.Empty));
    }

    [Fact]
    public void The_callback_reports_what_it_refused_and_whether_it_replaces_a_trusted_one()
    {
        using var certificate = SelfSigned("nms.example.net");
        CertificateDetails? reported = null;

        var callback = CertificateTrust.CreateCallback("nms.example.net", allowUntrusted: true, new[] { Pinned }, d => reported = d);
        var accepted = callback(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(accepted);
        Assert.NotNull(reported);
        Assert.Equal("nms.example.net", reported!.Host);
        Assert.Equal(CertificateTrust.Fingerprint(certificate), reported.Fingerprint);
        Assert.True(reported.ChainProblem);
        Assert.True(reported.ReplacesTrustedCertificate);
    }

    [Fact]
    public void The_callback_accepts_a_certificate_once_it_is_pinned()
    {
        using var certificate = SelfSigned("nms.example.net");
        var callback = CertificateTrust.CreateCallback("nms.example.net", allowUntrusted: true, new[] { CertificateTrust.Fingerprint(certificate) }, _ => throw new InvalidOperationException("Should not report an accepted certificate."));

        Assert.True(callback(new object(), certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void The_prompt_names_every_problem_and_shows_the_fingerprint()
    {
        var details = new CertificateDetails(
            "nms.example.net", "AA:BB", "CN=other.example.net", "CN=other.example.net",
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1),
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            ReplacesTrustedCertificate: false);

        var (title, message) = CertificateTrust.DescribeForPrompt(details, "LibreNMS");

        Assert.Equal("Trust LibreNMS's certificate?", title);
        Assert.Contains("isn't issued for nms.example.net", message);
        Assert.Contains("isn't from an authority", message);
        Assert.Contains("has expired", message);
        Assert.Contains("SHA-256:  AA:BB", message);
    }

    [Fact]
    public void A_changed_certificate_is_called_out_as_changed()
    {
        var details = new CertificateDetails(
            "nms.example.net", "AA:BB", "CN=nms.example.net", "CN=Example CA",
            DateTime.Now.AddDays(-1), DateTime.Now.AddDays(300),
            SslPolicyErrors.RemoteCertificateChainErrors, ReplacesTrustedCertificate: true);

        Assert.Equal("LibreNMS's certificate has changed", CertificateTrust.DescribeForPrompt(details, "LibreNMS").Title);
        Assert.Contains("changed since you trusted it", CertificateTrust.DescribeRejection(details, "LibreNMS"));
    }

    [Fact]
    public void A_certificate_waiting_for_the_user_never_triggers_failover()
    {
        var details = new CertificateDetails("nms.example.net", "AA", "CN=x", "CN=x", DateTime.Now, DateTime.Now, SslPolicyErrors.RemoteCertificateChainErrors, false);
        var ex = new LibreNmsApiException("untrusted", innerException: new HttpRequestException("tls", new System.Security.Authentication.AuthenticationException("certificate")))
        {
            UntrustedCertificate = details,
        };

        Assert.False(ServerFailover.IsUnreachable(ex));
    }

    private static X509Certificate2 SelfSigned(string host)
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
    }
}
