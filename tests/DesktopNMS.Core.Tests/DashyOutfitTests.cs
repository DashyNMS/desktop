using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class DashyOutfitTests
{
    [Fact]
    public void Every_hundred_possible_rolls_have_the_requested_distribution()
    {
        var outfits = Enumerable.Range(0, 100).Select(DashyOutfits.Pick).ToList();
        Assert.Equal(50, outfits.Count(o => o == DashyOutfit.Normal));
        Assert.Equal(20, outfits.Count(o => o == DashyOutfit.Bowtie));
        Assert.Equal(20, outfits.Count(o => o == DashyOutfit.Fedora));
        Assert.Equal(10, outfits.Count(o => o == DashyOutfit.Wizard));
    }
}
