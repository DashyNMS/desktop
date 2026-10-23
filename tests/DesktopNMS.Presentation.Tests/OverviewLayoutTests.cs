using DesktopNMS.Views.DeviceDetail;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class OverviewLayoutTests
{
    [Theory]
    [InlineData(320)]
    [InlineData(600)]
    [InlineData(900)]
    [InlineData(1500)]
    public void Cards_fit_without_overlap_at_different_window_widths(double width)
    {
        var cards = new (int, Func<double, double>)[]
        {
            (1, _ => 410), (2, _ => 280), (1, w => 40000 / w),
            (1, _ => 190), (1, _ => 240), (1, _ => 170), (1, _ => 330), (1, _ => 210)
        };
        var slots = OverviewLayoutPlan.Create(width, cards);
        foreach (var slot in slots)
        {
            Assert.True(slot.X >= 0 && slot.Y >= 0 && slot.Width > 0);
            Assert.True(slot.X + slot.Width <= width + 0.01);
        }
        for (var i = 0; i < slots.Length; i++)
        for (var j = i + 1; j < slots.Length; j++)
        {
            var a = slots[i]; var b = slots[j];
            Assert.True(a.X + a.Width <= b.X || b.X + b.Width <= a.X || a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
        }
    }

    [Fact]
    public void Shorter_columns_fill_before_the_tall_device_card_finishes()
    {
        var slots = OverviewLayoutPlan.Create(900, new (int, Func<double, double>)[]
        { (1, _ => 600), (2, _ => 200), (1, _ => 100), (1, _ => 100) });
        Assert.Equal(0, slots[1].Y);
        Assert.Equal(214, slots[2].Y);
        Assert.Equal(214, slots[3].Y);
        Assert.Empty(OverviewLayoutPlan.Create(900, Array.Empty<(int, Func<double, double>)>()));
    }
}
