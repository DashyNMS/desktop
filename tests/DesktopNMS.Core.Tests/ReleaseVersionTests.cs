using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0")]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("v1.0.0", "0.9.9")]
    [InlineData("v0.1.1", "0.1.0")]
    public void Newer_tag_is_detected(string candidateTag, string currentVersion)
    {
        Assert.True(ReleaseVersion.IsNewer(candidateTag, currentVersion));
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.2.0")]
    [InlineData("v0.0.9", "0.1.0")]
    public void Same_or_older_tag_is_not_newer(string candidateTag, string currentVersion)
    {
        Assert.False(ReleaseVersion.IsNewer(candidateTag, currentVersion));
    }

    [Theory]
    [InlineData(null, "0.1.0")]
    [InlineData("v0.2.0", null)]
    [InlineData("not-a-version", "0.1.0")]
    [InlineData("", "0.1.0")]
    public void Unparsable_input_is_never_treated_as_newer(string? candidateTag, string? currentVersion)
    {
        Assert.False(ReleaseVersion.IsNewer(candidateTag, currentVersion));
    }
}
