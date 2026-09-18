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

    // Issue #107: IsNewer used to discard the preview suffix from both sides
    // before comparing, so a newer preview was never detected as an update
    // over an older one already running.
    [Theory]
    [InlineData("v1.0.0-preview.2", "1.0.0-preview.1")]
    [InlineData("v1.0.0-preview.10", "1.0.0-preview.2")]
    [InlineData("v1.0.0", "1.0.0-preview.4")]
    public void Newer_preview_is_detected_relative_to_current_preview(string candidateTag, string currentVersion)
    {
        Assert.True(ReleaseVersion.IsNewer(candidateTag, currentVersion));
    }

    [Theory]
    [InlineData("v1.0.0-preview.1", "1.0.0-preview.2")]
    [InlineData("v1.0.0-preview.2", "1.0.0-preview.2")]
    public void Older_or_same_preview_is_not_newer(string candidateTag, string currentVersion)
    {
        Assert.False(ReleaseVersion.IsNewer(candidateTag, currentVersion));
    }

    [Theory]
    [InlineData("v1.0.0", "v0.9.9")]
    [InlineData("v1.0.0", "v1.0.0-preview.2")]
    [InlineData("v1.0.0-preview.2", "v1.0.0-preview.1")]
    [InlineData("v1.0.0-preview.10", "v1.0.0-preview.2")]
    public void Compare_ranks_a_above_b(string a, string b)
    {
        Assert.True(ReleaseVersion.Compare(a, b) > 0);
        Assert.True(ReleaseVersion.Compare(b, a) < 0);
    }

    [Fact]
    public void Compare_treats_equal_tags_as_equal()
    {
        Assert.Equal(0, ReleaseVersion.Compare("v1.0.0-preview.2", "1.0.0-preview.2"));
    }

    [Theory]
    [InlineData(null, "v1.0.0")]
    [InlineData("v1.0.0", null)]
    [InlineData("not-a-version", "v1.0.0")]
    public void Compare_is_unranked_for_unparsable_input(string? a, string? b)
    {
        Assert.Equal(0, ReleaseVersion.Compare(a, b));
    }
}
