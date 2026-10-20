using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using DesktopNMS.Core.Graphs;
using Microsoft.Win32;

namespace DesktopNMS.Infrastructure;

/// <summary>WPF's side of <see cref="UiPlatform"/>: the clipboard, the common file dialogs and the theme's resources (#249).</summary>
public static class WpfUiPlatform
{
    public static void Install() => UiPlatform.Use(new WpfClipboard(), new WpfFileDialogs(), new WpfGraphTheme(), new WpfThemeInfo(), new WpfKeyboard());

    private sealed class WpfThemeInfo : IAppThemeInfo
    {
        public DesktopNMS.Core.Configuration.AppTheme Effective => ThemeState.Effective;

        public bool SystemUsesLightTheme() => ThemeState.WindowsUsesLightTheme();
    }

    private sealed class WpfKeyboard : IKeyboardState
    {
        public bool IsControlDown => (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
    }

    private sealed class WpfClipboard : IClipboard
    {
        public bool SetText(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                // Another process briefly holds the clipboard.
                return false;
            }
        }
    }

    private sealed class WpfFileDialogs : IFileDialogs
    {
        public string? PickFileToOpen(FileDialogOptions options)
        {
            var dialog = new OpenFileDialog { Filter = options.Filter };
            Apply(dialog, options);
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? PickFileToSave(FileDialogOptions options)
        {
            var dialog = new SaveFileDialog { Filter = options.Filter };
            Apply(dialog, options);
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static void Apply(FileDialog dialog, FileDialogOptions options)
        {
            if (options.FileName is { } name)
            {
                dialog.FileName = name;
            }

            if (options.Title is { } title)
            {
                dialog.Title = title;
            }

            if (options.DefaultExtension is { } extension)
            {
                dialog.DefaultExt = extension;
            }
        }
    }

    /// <summary>The current theme's colours from the app's resources; anything missing falls back to the dark palette's.</summary>
    private sealed class WpfGraphTheme : IGraphTheme
    {
        public GraphPalette Palette
        {
            get
            {
                var resources = Application.Current?.Resources;
                var fallback = GraphPalette.Dark;
                if (resources is null)
                {
                    return fallback;
                }

                string Hex(string key, string otherwise) => resources[key] switch
                {
                    Color c => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
                    SolidColorBrush b => $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
                    _ => otherwise,
                };

                return fallback with
                {
                    Text = Hex("TextSecondaryColor", fallback.Text),
                    Grid = Hex("BorderColor", fallback.Grid),

                    // The brush, not the colour: a custom accent replaces the brush (AccentTheme).
                    Accent = Hex("AccentBrush", fallback.Accent),
                    Ok = Hex("OkColor", fallback.Ok),
                    Warning = Hex("WarningColor", fallback.Warning),
                    Critical = Hex("CriticalColor", fallback.Critical),
                };
            }
        }
    }
}
