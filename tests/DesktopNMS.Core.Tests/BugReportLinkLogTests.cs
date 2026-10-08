using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class BugReportLinkLogTests
{
    [Fact]
    public void A_new_bug_report_points_to_the_log_folder_and_asks_for_a_privacy_check()
    {
        var body = Uri.UnescapeDataString(BugReportLink.Build("1.1.0", "Windows 11", "24.9.0").Query);

        Assert.Contains("Open log folder", body);
        Assert.Contains("anything private", body);
        Assert.Contains("DashyNMS: 1.1.0", body);
    }
}
