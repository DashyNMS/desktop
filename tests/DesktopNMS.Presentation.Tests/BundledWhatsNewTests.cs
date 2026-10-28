using System.Reflection;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class BundledWhatsNewTests
{
    private const string Markdown = "## Changed\n- **Exact notes.** Available offline.\n\n## Downloads\n| Platform | File |\n";

    [Fact]
    public void The_build_embeds_only_its_declared_release_notes()
    {
        var assembly = typeof(BundledWhatsNew).Assembly;
        var declared = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ReleaseNotesVersion")?.Value;
        if (declared is null)
        {
            Assert.Null(BundledWhatsNew.Current);
            Assert.Null(assembly.GetManifestResourceStream("DesktopNMS.ReleaseNotes.md"));
            return;
        }
        var notes = Assert.IsType<DesktopNMS.Core.Updates.WhatsNewNotes>(BundledWhatsNew.Current);
        Assert.Equal(declared, notes.Version);
        var built = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.True(BundledWhatsNew.Matches(notes, built));
        Assert.False(string.IsNullOrWhiteSpace(notes.Summary()));
        using var stream = assembly.GetManifestResourceStream("DesktopNMS.ReleaseNotes.md");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var markdown = reader.ReadToEnd();
        if (Environment.GetEnvironmentVariable("DASHYNMS_EXPECTED_RELEASE_NOTES") is { Length: > 0 } expectedFile)
            Assert.Equal(File.ReadAllText(expectedFile), markdown);
        var parsed = BundledWhatsNew.ParseRelease(declared, markdown);
        Assert.NotNull(parsed);
        Assert.Equal(notes.Version, parsed.Version);
        Assert.Equal(notes.Sections.SelectMany(s => s.Items), parsed.Sections.SelectMany(s => s.Items));
    }

    [Theory]
    [InlineData("2.0.0-preview.10", "2.0.0-preview.9")]
    [InlineData("2.0.0-preview.11", "2.0.0-preview.10")]
    [InlineData("2.0.0", "2.0.0-preview.11")]
    [InlineData("2.1.0", "2.0.0")]
    [InlineData("2.0.0-preview.11", null)]
    public void A_new_exact_release_is_due_until_dismissed(string version, string? previouslyShown)
    {
        var notes = BundledWhatsNew.ParseRelease(version, Markdown);
        Assert.NotNull(notes);
        Assert.True(BundledWhatsNew.IsDue(notes, version, previouslyShown));
        Assert.False(BundledWhatsNew.IsDue(notes, version, version));
        Assert.False(BundledWhatsNew.IsDue(notes, "v" + version + "+commit", "v" + version));
    }

    [Theory]
    [InlineData("2.0.0-preview.10")]
    [InlineData("2.0.0")]
    [InlineData("2.0.0-preview.pr.544.86.1")]
    [InlineData("2.0.0-preview.ci.123.1")]
    [InlineData("2.0.0-dev.local")]
    [InlineData("unexpected-version")]
    public void Other_releases_and_development_versions_do_not_show_preview_11_notes(string running)
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown);
        Assert.False(BundledWhatsNew.IsDue(notes, running, null));
    }

    [Theory]
    [InlineData("2.0.0-preview.pr.544.86.1")]
    [InlineData("2.0.0-preview.ci.123.1")]
    [InlineData("2.0.0-dev.local")]
    public void Development_builds_do_not_parse_published_notes(string version)
        => Assert.Null(BundledWhatsNew.ParseRelease(version, Markdown));

    [Fact]
    public void Offline_notes_include_the_exact_changes_without_empty_download_tabs()
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown)!;
        Assert.Equal("2.0.0-preview.11", notes.Version);
        var section = Assert.Single(notes.Sections);
        Assert.Equal("Changed", section.Name);
        var item = Assert.Single(section.Items);
        Assert.Equal("Exact notes", item.Title);
        Assert.Equal("Available offline.", item.Text);
    }

    [Fact]
    public void Manual_reopening_does_not_depend_on_automatic_seen_state()
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown)!;
        Assert.False(BundledWhatsNew.IsDue(notes, notes.Version, notes.Version));
        var model = new WhatsNewViewModel(notes, new Uri("https://example.net/releases/preview.11"), _ => { });
        Assert.Contains("2.0.0-preview.11", model.Title);
        Assert.Single(model.Tabs);
    }

    [Fact]
    public void Automatic_presentation_waits_for_readiness_and_coalesces_requests()
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown)!;
        var presenter = new ReleaseNotesPresenter();
        var ready = false; string? seen = null; var shown = 0;
        var queued = new Queue<Action>();
        void Schedule() => presenter.TrySchedule(notes, notes.Version, () => seen, () => ready, queued.Enqueue,
            () => { shown++; seen = notes.Version; });
        Schedule(); Assert.Empty(queued);
        ready = true; Schedule(); Schedule(); Assert.Single(queued);
        ready = false; queued.Dequeue()(); Assert.Equal(0, shown);
        ready = true; Schedule(); queued.Dequeue()(); Assert.Equal(1, shown);
        Schedule(); Assert.Empty(queued);
    }

    [Fact]
    public void Dismissal_elsewhere_cancels_an_already_queued_announcement()
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown)!;
        string? seen = null; Action pending = null!; var shown = 0;
        new ReleaseNotesPresenter().TrySchedule(notes, notes.Version, () => seen, () => true, action => pending = action, () => shown++);
        seen = notes.Version;
        pending();
        Assert.Equal(0, shown);
    }
    [Fact]
    public void A_failed_dialog_can_be_scheduled_again_without_marking_the_version_seen()
    {
        var notes = BundledWhatsNew.ParseRelease("2.0.0-preview.11", Markdown)!;
        var presenter = new ReleaseNotesPresenter();
        var queued = new Queue<Action>();
        string? seen = null; var attempts = 0;
        void Schedule() => presenter.TrySchedule(notes, notes.Version, () => seen, () => true, queued.Enqueue, () =>
        {
            if (++attempts == 1) throw new IOException("Synthetic dialog failure.");
            seen = notes.Version;
        });
        Schedule();
        Assert.Throws<IOException>(queued.Dequeue());
        Assert.Null(seen);
        Schedule(); queued.Dequeue()();
        Assert.Equal(notes.Version, seen);
    }

}
