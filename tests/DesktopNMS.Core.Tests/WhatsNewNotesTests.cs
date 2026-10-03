using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Updates;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class WhatsNewNotesTests
{
    private const string Sample = """
        # 1.1.0
        Released 18 October 2026

        ## New
        - **Maps.** Network, geographical and custom maps. {map}
        - **Neighbours:** Every neighbour at a glance,
          filtered down with Neighbourhoods.

        ## Improved
        - **Widget picker.** Categories and search.

        ## Fixed
        - Plain bullet without a lead-in
        """;

    [Fact]
    public void Reads_version_date_sections_and_items()
    {
        var notes = WhatsNewNotes.Parse(Sample)!;

        Assert.Equal("1.1.0", notes.Version);
        Assert.Equal("Released 18 October 2026", notes.Released);
        Assert.Equal(new[] { "New", "Improved", "Fixed" }, notes.Sections.Select(s => s.Name));
        Assert.Equal(4, notes.Count);

        var maps = notes.Sections[0].Items[0];
        Assert.Equal("Maps", maps.Title);
        Assert.Equal("Network, geographical and custom maps.", maps.Text);
        Assert.Equal("map", maps.Icon);
    }

    [Fact]
    public void Indented_lines_carry_a_bullet_on_and_a_colon_lead_in_is_a_title()
    {
        var item = WhatsNewNotes.Parse(Sample)!.Sections[0].Items[1];

        Assert.Equal("Neighbours", item.Title);
        Assert.Equal("Every neighbour at a glance, filtered down with Neighbourhoods.", item.Text);
        Assert.Null(item.Icon);
    }

    [Fact]
    public void A_bullet_without_a_lead_in_is_all_title()
    {
        var item = WhatsNewNotes.Parse(Sample)!.Sections[2].Items[0];

        Assert.Equal("Plain bullet without a lead-in", item.Title);
        Assert.Equal(string.Empty, item.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("## New\n- **Maps.** No version heading")]
    [InlineData("# 1.1.0\n## New\n")]
    public void Nothing_usable_is_null(string? markdown) => Assert.Null(WhatsNewNotes.Parse(markdown));

    [Fact]
    public void Summary_names_the_first_changes_and_counts_the_rest()
    {
        var notes = WhatsNewNotes.Parse(Sample)!;

        Assert.Equal("Maps, Neighbours, Widget picker and 1 more change", notes.Summary());
        Assert.Equal("Maps and 3 more changes", notes.Summary(1));
        Assert.Equal("Maps, Neighbours, Widget picker and Plain bullet without a lead-in", notes.Summary(4));
    }

    [Theory]
    [InlineData("1.1.0", null, true)]
    [InlineData("1.1.0", "1.0.2", true)]
    [InlineData("1.1.0", "1.1.0-preview.7", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.1.0", "v1.1.0", false)]
    [InlineData("1.1.0-preview.8", null, false)]
    [InlineData("1.1.1", null, false)]
    public void Due_once_per_release_and_only_with_its_own_notes(string current, string? shown, bool due)
        => Assert.Equal(due, WhatsNewNotes.IsDue(WhatsNewNotes.Parse(Sample), current, shown));

    [Fact]
    public void A_fresh_install_counts_this_version_as_seen()
    {
        var settings = new AppSettings();

        Assert.True(WhatsNewNotes.MarkSeenIfFreshInstall(settings, "1.1.0"));
        Assert.Equal("1.1.0", settings.WhatsNewShownVersion);
    }

    [Fact]
    public void An_upgrade_with_a_server_saved_still_sees_it()
    {
        // 1.0 never recorded the version it ran, so a saved server is what marks an upgrade.
        var settings = new AppSettings { ServerUrl = "https://nms.example.net/" };

        Assert.False(WhatsNewNotes.MarkSeenIfFreshInstall(settings, "1.1.0"));
        Assert.Null(settings.WhatsNewShownVersion);
    }
}
