using DesktopNMS.Core.Configuration;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class StarterDashboardTests
{
    [Fact]
    public void Every_widget_is_a_known_type_with_a_fresh_id()
    {
        var widgets = StarterDashboard.Create();

        Assert.All(widgets, w => Assert.True(DashboardWidgetTypes.IsKnown(w.WidgetType)));
        Assert.Equal(widgets.Count, widgets.Select(w => w.Id).Distinct().Count());
        Assert.NotEqual(StarterDashboard.Create()[0].Id, widgets[0].Id);
    }

    [Fact]
    public void No_two_widgets_overlap_and_each_is_at_least_the_minimum_size()
    {
        var widgets = StarterDashboard.Create();

        Assert.All(widgets, w =>
        {
            Assert.True(w.ColumnSpan >= DashboardWidget.MinColumnSpan);
            Assert.True(w.RowSpan >= DashboardWidget.MinRowSpan);
        });

        for (var i = 0; i < widgets.Count; i++)
        {
            for (var j = i + 1; j < widgets.Count; j++)
            {
                var (a, b) = (widgets[i], widgets[j]);
                var overlap = a.Column < b.Column + b.ColumnSpan && b.Column < a.Column + a.ColumnSpan
                              && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;
                Assert.False(overlap, $"{a.Title} overlaps {b.Title}");
            }
        }
    }
}
