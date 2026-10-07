using DesktopNMS.Core.Licences;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class OpenSourceNoticesTests
{
    [Fact]
    public void Every_licence_text_a_shared_entry_names_ships_in_Core()
    {
        foreach (var notice in OpenSourceNotices.Shared.Where(n => n.LicenceFile is not null))
        {
            Assert.Contains(notice.LicenceFile!, OpenSourceNotices.LicenceFiles);
        }
    }

    [Fact]
    public void Ships_every_licence_text_either_app_names()
    {
        string[] expected =
        [
            OpenSourceNotices.Mit, OpenSourceNotices.Apache2, OpenSourceNotices.Bsd2Leaflet, OpenSourceNotices.Bsd3SharpVectors,
            OpenSourceNotices.Bsd3WebView2, OpenSourceNotices.OflIbmPlex, OpenSourceNotices.OflSora,
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), OpenSourceNotices.LicenceFiles);
        Assert.All(expected, file => Assert.False(string.IsNullOrWhiteSpace(OpenSourceNotices.ReadLicence(file))));
    }

    [Fact]
    public void A_licence_Core_does_not_have_is_null()
        => Assert.Null(OpenSourceNotices.ReadLicence("gpl.txt"));

    [Fact]
    public void Each_platform_gets_only_what_it_ships()
    {
        OpenSourceNotice[] notices =
        [
            new("Everywhere", "x", "c", "MIT", new Uri("https://example.net/"), OpenSourceNotices.Mit),
            new("AndroidX", "x", "c", "Apache 2.0", new Uri("https://example.net/"), OpenSourceNotices.Apache2, NoticePlatforms.Android),
            new("WebView2", "x", "c", "BSD", new Uri("https://example.net/"), OpenSourceNotices.Bsd3WebView2, NoticePlatforms.Windows),
        ];

        Assert.Equal(new[] { "Everywhere", "WebView2" }, OpenSourceNotices.For(NoticePlatforms.Windows, notices).Select(n => n.Name));
        Assert.Equal(new[] { "Everywhere" }, OpenSourceNotices.For(NoticePlatforms.iOS, notices).Select(n => n.Name));
    }

    [Fact]
    public void The_trademark_line_names_both_integrations()
    {
        Assert.Contains("LibreNMS", OpenSourceNotices.Trademarks);
        Assert.Contains("Graylog", OpenSourceNotices.Trademarks);
    }
}
