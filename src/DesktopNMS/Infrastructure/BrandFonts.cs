using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// The app's typefaces (#216) for code that draws its own text - the map
/// canvases, the neighbour graph, the release notes viewer - matching the
/// BodyFont, DisplayFont and MonoFont theme resources: the same files
/// DashyNMS Mobile ships, bundled in Assets/Fonts, each falling back to the
/// Windows font it replaced.
/// </summary>
public static class BrandFonts
{
    private static readonly Uri FontsFolder = new("pack://application:,,,/Assets/Fonts/");

    /// <summary>IBM Plex Sans, for text (Regular and SemiBold).</summary>
    public static FontFamily Body { get; } = new(FontsFolder, "./#IBM Plex Sans, Segoe UI");

    /// <summary>Sora (Bold), for headings, big numbers and the wordmark.</summary>
    public static FontFamily Display { get; } = new(FontsFolder, "./#Sora, Segoe UI");

    /// <summary>IBM Plex Mono, for config, diffs and code.</summary>
    public static FontFamily Mono { get; } = new(FontsFolder, "./#IBM Plex Mono, Consolas");

    private static bool _applied;

    /// <summary>
    /// Makes <see cref="Body"/> every element's default font - anything not
    /// styled otherwise, in every window. WPF only lets this be set once per
    /// type, so it's guarded; call it at startup, before any window opens.
    /// </summary>
    public static void ApplyAsDefault()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        // Windows pass their font down to everything inside them; menus and
        // tooltips open in trees of their own, so they get it directly.
        Override(TextElement.FontFamilyProperty, typeof(TextElement));
        Override(System.Windows.Controls.TextBlock.FontFamilyProperty, typeof(System.Windows.Controls.TextBlock));
        Override(System.Windows.Controls.Control.FontFamilyProperty, typeof(Window));
        Override(System.Windows.Controls.Control.FontFamilyProperty, typeof(System.Windows.Controls.ContextMenu));
        Override(System.Windows.Controls.Control.FontFamilyProperty, typeof(System.Windows.Controls.ToolTip));
    }

    /// <summary>A font is cosmetic: if WPF refuses an override, carry on with the theme's own styles rather than fail to start.</summary>
    private static void Override(DependencyProperty property, Type type)
    {
        try
        {
            property.OverrideMetadata(type, new FrameworkPropertyMetadata(Body, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
    }
}
