using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Updates;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>Exact release notes embedded from docs/releases; development builds carry no published-release notes.</summary>
public static class BundledWhatsNew
{
    private static readonly Regex PublishedVersion = new(@"^\d+\.\d+\.\d+(-preview\.\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Lazy<WhatsNewNotes?> Notes = new(Load);

    public static WhatsNewNotes? Current => Notes.Value;

    public static WhatsNewNotes? ForVersion(string version) => Matches(Current, version) ? Current : null;

    /// <summary>Desktop previews have their own identity; Core's older stable-only policy remains unchanged for other consumers.</summary>
    public static bool IsDue(WhatsNewNotes? notes, string currentVersion, string? shownVersion) =>
        Matches(notes, currentVersion) && !string.Equals(Normalise(shownVersion), Normalise(currentVersion), StringComparison.OrdinalIgnoreCase);

    public static bool Matches(WhatsNewNotes? notes, string version) => notes is not null
        && PublishedVersion.IsMatch(Normalise(version))
        && string.Equals(Normalise(notes.Version), Normalise(version), StringComparison.OrdinalIgnoreCase);

    internal static string Normalise(string? version) => (version ?? "").Trim().TrimStart('v', 'V').Split('+')[0];

    internal static WhatsNewNotes? ParseRelease(string version, string markdown)
    {
        if (!PublishedVersion.IsMatch(version)) return null;
        // Release files are the source used by GitHub publishing. Supply their
        // version to the shared parser without maintaining a second changelog.
        var notes = WhatsNewNotes.Parse("# " + version + "\n\n" + markdown);
        return notes is null ? null : notes with { Version = version, Released = null };
    }

    private static WhatsNewNotes? Load()
    {
        var assembly = typeof(BundledWhatsNew).Assembly;
        var version = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "ReleaseNotesVersion")?.Value;
        if (version is null) return null;
        using var stream = assembly.GetManifestResourceStream("DesktopNMS.ReleaseNotes.md");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return ParseRelease(version, reader.ReadToEnd());
    }
}

/// <summary>How a section is coloured: New in the accent, Improved green, Fixed amber.</summary>
public enum WhatsNewTone
{
    New,
    Improved,
    Fixed,
}

/// <summary>
/// The "What's new" dialog (#227): the release's notes as a tab per section,
/// each change a card. Shown once per release after the first connection,
/// and from Settings, About.
/// </summary>
public sealed class WhatsNewViewModel : ObservableObject
{
    private WhatsNewTabViewModel _selectedTab;

    public WhatsNewViewModel(WhatsNewNotes notes, Uri releaseNotesUrl, Action<Uri> openUrl)
    {
        Title = "What's new in " + notes.Version;
        Released = notes.Released;
        Tabs = notes.Sections.Select(s => new WhatsNewTabViewModel(s, this)).ToList();
        _selectedTab = Tabs[0];
        _selectedTab.IsSelected = true;
        OpenReleaseNotesCommand = new RelayCommand(() => openUrl(releaseNotesUrl));
    }

    public string Title { get; }

    /// <summary>e.g. "Released 18 October 2026", if the notes say.</summary>
    public string? Released { get; }

    public bool HasReleased => !string.IsNullOrWhiteSpace(Released);

    public IReadOnlyList<WhatsNewTabViewModel> Tabs { get; }

    public WhatsNewTabViewModel SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (ReferenceEquals(_selectedTab, value))
            {
                return;
            }

            _selectedTab.IsSelected = false;
            _selectedTab = value;
            _selectedTab.IsSelected = true;
            OnPropertyChanged();
        }
    }

    public RelayCommand OpenReleaseNotesCommand { get; }

    internal void Select(WhatsNewTabViewModel tab) => SelectedTab = tab;
}

public sealed class WhatsNewTabViewModel : ObservableObject
{
    private bool _isSelected;

    public WhatsNewTabViewModel(WhatsNewSection section, WhatsNewViewModel owner)
    {
        Name = section.Name;
        Tone = section.Name.Trim().ToLowerInvariant() switch
        {
            "improved" or "improvements" or "changed" => WhatsNewTone.Improved,
            "fixed" or "fixes" or "bug fixes" => WhatsNewTone.Fixed,
            _ => WhatsNewTone.New,
        };
        Items = section.Items.Select(i => new WhatsNewItemViewModel(i, Tone)).ToList();
        SelectCommand = new RelayCommand(() => owner.Select(this));
    }

    public string Name { get; }

    public WhatsNewTone Tone { get; }

    public int Count => Items.Count;

    public IReadOnlyList<WhatsNewItemViewModel> Items { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class WhatsNewItemViewModel
{
    /// <summary>Icon names release notes can use (a trailing "{name}"), as Segoe Fluent Icons glyphs.</summary>
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map"] = "",
        ["link"] = "",
        ["logs"] = "",
        ["copy"] = "",
        ["add"] = "",
        ["swap"] = "",
        ["chart"] = "",
        ["download"] = "",
        ["look"] = "",
        ["grid"] = "",
        ["tray"] = "",
        ["lock"] = "",
        ["speed"] = "",
        ["clock"] = "",
        ["shield"] = "",
        ["signout"] = "",
        ["package"] = "",
        ["bell"] = "",
        ["device"] = "",
        ["settings"] = "",
        ["search"] = "",
        ["star"] = "",
        ["wrench"] = "",
    };

    public WhatsNewItemViewModel(WhatsNewItem item, WhatsNewTone tone)
    {
        Title = item.Title;
        Text = item.Text;
        Tone = tone;
        Icon = item.Icon is { } named && Glyphs.ContainsKey(named)
            ? named.ToLowerInvariant()
            : tone switch
            {
                WhatsNewTone.Improved => "check",
                WhatsNewTone.Fixed => "wrench",
                _ => "star",
            };
        Glyph = item.Icon is { } icon && Glyphs.TryGetValue(icon, out var glyph)
            ? glyph
            : tone switch
            {
                WhatsNewTone.Improved => "",
                WhatsNewTone.Fixed => "",
                _ => "",
            };
    }

    public string Title { get; }

    public string Text { get; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public WhatsNewTone Tone { get; }

    public string Glyph { get; }

    /// <summary>The icon's name ("map", "bell"; "star", "check" or "wrench" by tone) - for a front end without Segoe Fluent Icons (#250).</summary>
    public string Icon { get; }
}
