using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopNMS.Core.Updates;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>The "What's new" notes built into this copy of DashyNMS (WhatsNew.md, embedded at build time).</summary>
public static class BundledWhatsNew
{
    private static readonly Lazy<WhatsNewNotes?> Notes = new(Load);

    public static WhatsNewNotes? Current => Notes.Value;

    private static WhatsNewNotes? Load()
    {
        using var stream = typeof(BundledWhatsNew).Assembly.GetManifestResourceStream("DesktopNMS.WhatsNew.md");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return WhatsNewNotes.Parse(reader.ReadToEnd());
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
    /// <summary>Icon names WhatsNew.md can use (a trailing "{name}"), as Segoe Fluent Icons glyphs.</summary>
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
}
