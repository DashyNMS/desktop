using DesktopNMS.Services.Intelligence;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Why the network tools didn't start, from the MCP server's own wording, as one safe word (#325).</summary>
public class McpStartReasonTests
{
    [Theory]
    [InlineData("The certificate nms.example.net presents isn't trusted by this machine. Subject: CN=nms.example.net; issuer: CN=Example CA; valid 2026-01-01 to 2027-01-01. If it is your LibreNMS server's own certificate, add this fingerprint to LIBRENMS_TRUSTED_CERTS: AB:CD", McpStartReason.Certificate)]
    [InlineData("The certificate nms.example.net presents isn't one listed in LIBRENMS_TRUSTED_CERTS. It may have been renewed, or the connection may be intercepted.", McpStartReason.Certificate)]
    [InlineData("LibreNMS at https://nms.example.net/ refused the API token. Check LIBRENMS_TOKEN.", McpStartReason.Unauthorised)]
    [InlineData("Couldn't connect to LibreNMS at https://nms.example.net/: No such host is known.", McpStartReason.Unreachable)]
    [InlineData("LIBRENMS_URL is not set. Set it to the address of your LibreNMS server, e.g. https://librenms.example.com.", McpStartReason.Config)]
    [InlineData("GRAYLOG_VERSION: '3' isn't supported. Use 2.1 for Graylog 2.1 or newer, or 2.0.", McpStartReason.Config)]
    [InlineData("Set both UNIMUS_URL and UNIMUS_TOKEN to use Unimus, or neither.", McpStartReason.Config)]
    [InlineData("Unhandled exception. System.IO.IOException: something else entirely", McpStartReason.Unknown)]
    public void The_servers_reason_becomes_one_word(string line, string expected)
    {
        Assert.Equal(expected, McpStartReason.Classify(new[] { "info: starting", line }));
    }

    [Fact]
    public void A_plain_http_warning_is_not_the_reason()
    {
        var lines = new[]
        {
            "Warning: LIBRENMS_URL uses http://, so the API token crosses the network unencrypted. Use https:// if the server supports it.",
            "LibreNMS at http://nms.example.net/ refused the API token. Check LIBRENMS_TOKEN.",
        };

        Assert.Equal(McpStartReason.Unauthorised, McpStartReason.Classify(lines));
        Assert.Equal(McpStartReason.Unknown, McpStartReason.Classify(lines.Take(1)));
    }

    [Fact]
    public void Every_reason_but_unknown_has_its_own_message_without_the_servers_text()
    {
        foreach (var reason in new[] { McpStartReason.Certificate, McpStartReason.Unauthorised, McpStartReason.Unreachable, McpStartReason.Config })
        {
            var message = McpStartReason.Describe(reason);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("example", message, StringComparison.Ordinal);
        }

        Assert.Null(McpStartReason.Describe(McpStartReason.Unknown));
    }
}
