using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class BundledWhatsNewTests
{
    [Fact]
    public void Both_front_ends_can_read_the_bundled_release_notes()
    {
        var notes = BundledWhatsNew.Current;
        Assert.NotNull(notes);
        Assert.False(string.IsNullOrWhiteSpace(notes.Version));
        Assert.False(string.IsNullOrWhiteSpace(notes.Summary()));
    }
}
