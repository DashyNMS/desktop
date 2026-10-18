using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceToolObservationTests
{
    [Fact]
    public void Receipt_uses_only_the_tools_summary_and_observation_time()
    {
        var observed = DateTimeOffset.UtcNow;
        var receipt = IntelligenceToolObservation.FromResult("active_alerts", new("""
            {"status":"ok","summary":"3 critical and 7 warning alerts returned.","data":{"private":"synthetic-private-token"}}
            """, false), observed);
        Assert.Equal("3 critical and 7 warning alerts returned.", receipt.Summary);
        Assert.Equal(observed, receipt.ObservedAt);
        Assert.False(receipt.IsError);
        Assert.DoesNotContain("synthetic-private-token", receipt.Summary);
    }

    [Theory]
    [InlineData("{\"status\":\"error\",\"message\":\"synthetic-private-token\"}", false)]
    [InlineData("synthetic-private-token", true)]
    public void Failed_lookups_never_expose_raw_error_text(string output, bool error)
    {
        var receipt = IntelligenceToolObservation.FromResult("active_alerts", new(output, error), DateTimeOffset.UtcNow);
        Assert.True(receipt.IsError);
        Assert.DoesNotContain("synthetic-private-token", receipt.Summary);
    }

    [Theory]
    [InlineData("{invalid json")]
    [InlineData("{\"status\":123,\"data\":\"synthetic-private-token\"}")]
    public void Unsupported_output_uses_a_generic_receipt_instead_of_raw_data(string output)
        => Assert.Equal("Lookup completed.", IntelligenceToolObservation.FromResult("device_health", new(output, false), DateTimeOffset.UtcNow).Summary);

    [Fact]
    public void Display_summary_is_bounded()
    {
        var output = "{\"status\":\"ok\",\"summary\":\"" + new string('x', 1000) + "\"}";
        Assert.Equal(350, IntelligenceToolObservation.FromResult("device_health", new(output, false), DateTimeOffset.UtcNow).Summary.Length);
    }
}
