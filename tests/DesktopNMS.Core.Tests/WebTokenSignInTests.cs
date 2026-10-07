using DesktopNMS.Core.Api;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.SignIn;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>"Sign in with LibreNMS" - moved from DashyNMS Mobile (mobile#162), its reference implementation.</summary>
public sealed class WebTokenSignInTests
{
    private static readonly Uri Root = new("https://nms.example.com/");
    private const string SanctumToken = "12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd";

    private static WebTokenSignIn NewFlow() => new(Root, "DashyNMS · iPhone · 7 Oct 2026");

    private static Uri At(string path) => new(Root, path);

    [Fact]
    public void Starts_on_the_API_Tokens_page() =>
        Assert.Equal("https://nms.example.com/api-access", NewFlow().TokensPage.AbsoluteUri);

    [Fact]
    public void Starts_under_an_install_in_a_sub_path() =>
        Assert.Equal("https://example.com/librenms/api-access", new WebTokenSignIn(new Uri("https://example.com/librenms/"), "x").TokensPage.AbsoluteUri);

    [Fact]
    public void Leaves_the_login_page_to_the_user() =>
        Assert.Equal(WebSignInAction.Wait, NewFlow().Next(At("login"), "login").Action);

    [Fact]
    public void Submits_the_form_once_then_reads_the_token()
    {
        var flow = NewFlow();

        Assert.Equal(WebSignInAction.Submit, flow.Next(At("api-access"), "form").Action);

        var done = flow.Next(At("api-access"), "token:" + SanctumToken);
        Assert.Equal(WebSignInAction.Done, done.Action);
        Assert.Equal(SanctumToken, done.Token);
    }

    [Fact]
    public void Fails_when_the_form_comes_back_without_a_token()
    {
        var flow = NewFlow();
        flow.Next(At("api-access"), "form");

        Assert.Equal(WebSignInAction.Failed, flow.Next(At("api-access"), "form").Action);
    }

    [Fact]
    public void Goes_to_the_tokens_page_when_signing_in_lands_on_the_dashboard() =>
        Assert.Equal(WebSignInAction.GoToTokensPage, NewFlow().Next(At("overview"), "signed-in").Action);

    [Fact]
    public void Stops_sending_the_page_back_after_a_few_tries()
    {
        var flow = NewFlow();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(WebSignInAction.GoToTokensPage, flow.Next(At("overview"), "signed-in").Action);
        }

        Assert.Equal(WebSignInAction.Wait, flow.Next(At("overview"), "signed-in").Action);
    }

    [Theory]
    [InlineData("signed-in")]
    [InlineData("other")]
    public void A_tokens_page_without_the_form_means_no_API_access(string probe) =>
        Assert.Equal(WebSignInAction.NotAllowed, NewFlow().Next(At("api-access"), probe).Action);

    [Fact]
    public void Two_factor_is_left_to_the_user() =>
        Assert.Equal(WebSignInAction.Wait, NewFlow().Next(At("2fa"), "other").Action);

    [Fact]
    public void Another_site_is_never_driven()
    {
        // A sign-on provider's page, even one that looks like the form.
        Assert.Equal(WebSignInAction.Wait, NewFlow().Next(new Uri("https://sso.example.com/api-access"), "form").Action);
        Assert.Equal(WebSignInAction.Wait, NewFlow().Next(new Uri("http://nms.example.com/api-access"), "form").Action);
        Assert.Equal(WebSignInAction.Wait, NewFlow().Next(new Uri("https://nms.example.com:8443/api-access"), "form").Action);
    }

    [Fact]
    public void Something_that_isnt_a_token_is_a_failure() =>
        Assert.Equal(WebSignInAction.Failed, NewFlow().Next(At("api-access"), "token:<b>oops</b>").Action);

    [Theory]
    [InlineData(SanctumToken, true)]
    [InlineData("3|librenms_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd1a2b3c4d", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("12|short", false)]
    [InlineData("12|has space in it and is long enough", false)]
    [InlineData("", false)]
    public void Recognises_tokens(string text, bool expected) =>
        Assert.Equal(expected, ApiTokenText.IsToken(text));

    [Theory]
    [InlineData("\"form\"", "form")]
    [InlineData("form", "form")]
    [InlineData("\"token:12|abc\"", "token:12|abc")]
    [InlineData("null", "")]
    [InlineData(null, "")]
    public void Reads_script_results_from_either_platform(string? raw, string expected) =>
        Assert.Equal(expected, WebTokenSignIn.Unwrap(raw));

    [Fact]
    public void Names_the_token_after_the_device_and_the_day() =>
        Assert.Equal("DashyNMS · Tom's iPhone · 7 Oct 2026", WebTokenSignIn.NameFor("Tom's iPhone", new DateTime(2026, 10, 7)));

    [Fact]
    public void Names_a_nameless_device_and_keeps_within_LibreNMS_limit()
    {
        Assert.Equal("DashyNMS · device · 7 Oct 2026", WebTokenSignIn.NameFor(" ", new DateTime(2026, 10, 7)));
        Assert.Equal(255, WebTokenSignIn.NameFor(new string('x', 400), new DateTime(2026, 10, 7)).Length);
    }

    [Fact]
    public void The_submit_script_quotes_the_name_safely()
    {
        var script = new WebTokenSignIn(Root, "Tom's \"phone\"</script>").SubmitScript;

        Assert.Contains("d.value=\"Tom\\u0027s \\u0022phone\\u0022\\u003C/script\\u003E\"", script);
    }
}

public sealed class ApiTokenTextTests
{
    [Theory]
    [InlineData("  12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd\r\n", "12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd")]
    [InlineData("Bearer 12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd", "12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    public void Takes_the_token_out_of_what_was_copied(string copied, string expected)
    {
        Assert.True(ApiTokenText.TryExtract(copied, out var token));
        Assert.Equal(expected, token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://nms.example.com/")]
    [InlineData("my password")]
    public void Anything_else_is_not_a_token(string? copied)
    {
        Assert.False(ApiTokenText.TryExtract(copied, out var token));
        Assert.Equal(string.Empty, token);
    }
}

public sealed class CertificateTrustIsTrustedTests
{
    [Fact]
    public void A_web_views_certificate_is_trusted_by_its_fingerprint()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=nms.example.com", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var der = certificate.RawData;
        var fingerprint = CertificateTrust.Fingerprint(certificate);

        Assert.True(CertificateTrust.IsTrusted([fingerprint.ToLowerInvariant().Replace(":", " ", StringComparison.Ordinal)], der));
        Assert.False(CertificateTrust.IsTrusted(["AB:CD"], der));
        Assert.False(CertificateTrust.IsTrusted([fingerprint], null));
        Assert.False(CertificateTrust.IsTrusted([], der));
    }
}

public sealed class ApiTokensPageUrlTests
{
    [Theory]
    [InlineData("nms.example.com", "https://nms.example.com/api-access")]
    [InlineData("https://nms.example.com:8443", "https://nms.example.com:8443/api-access")]
    [InlineData("http://192.0.2.10/librenms/", "http://192.0.2.10/librenms/api-access")]
    [InlineData("https://nms.example.com/librenms/api/v0", "https://nms.example.com/librenms/api-access")]
    public void Opens_the_tokens_page_under_the_web_root(string address, string expected)
        => Assert.Equal(expected, LibreNmsConnection.ApiTokensPageUrl(address)?.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://nms.example.com")]
    public void Nothing_until_it_is_an_address(string? address)
        => Assert.Null(LibreNmsConnection.ApiTokensPageUrl(address));
}
