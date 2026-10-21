using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graphs;

namespace DesktopNMS.Infrastructure;

/// <summary>Avalonia's side of <see cref="UiPlatform"/> (#250): the clipboard, file dialogs, theme and keyboard, as WpfUiPlatform is WPF's.</summary>
public static class AvaloniaUiPlatform
{
    public static void Install()
    {
        // Avalonia has no static keyboard state, so the app keeps its own from
        // every window's key events.
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, e) => Keyboard.Modifiers = e.KeyModifiers, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>((_, e) => Keyboard.Modifiers = e.KeyModifiers, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, e) => Keyboard.Modifiers = e.KeyModifiers, handledEventsToo: true);

        UiPlatform.Use(new AvaloniaClipboard(), new AvaloniaFileDialogs(), new AvaloniaGraphTheme(), new AvaloniaThemeInfo(), Keyboard);
    }

    private static readonly KeyboardTracker Keyboard = new();

    /// <summary>The window to put dialogs over: the active one, else the main one.</summary>
    internal static TopLevel? ActiveTopLevel()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    private sealed class KeyboardTracker : IKeyboardState
    {
        public KeyModifiers Modifiers { get; set; }

        public bool IsControlDown => Modifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
    }

    private sealed class AvaloniaClipboard : IClipboard
    {
        public bool SetText(string text)
        {
            if (ActiveTopLevel()?.Clipboard is not { } clipboard)
            {
                return false;
            }

            _ = clipboard.SetTextAsync(text);
            return true;
        }
    }

    private sealed class AvaloniaFileDialogs : IFileDialogs
    {
        public string? PickFileToOpen(FileDialogOptions options)
        {
            if (ActiveTopLevel()?.StorageProvider is not { } storage)
            {
                return null;
            }

            var files = Modal.Wait(storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = false,
                FileTypeFilter = Filters(options.Filter),
            }));
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        public string? PickFileToSave(FileDialogOptions options)
        {
            if (ActiveTopLevel()?.StorageProvider is not { } storage)
            {
                return null;
            }

            var file = Modal.Wait(storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = options.Title,
                SuggestedFileName = options.FileName,
                DefaultExtension = options.DefaultExtension,
                FileTypeChoices = Filters(options.Filter),
            }));
            return file?.TryGetLocalPath();
        }

        /// <summary>A WPF filter ("CSV files (*.csv)|*.csv|All files (*.*)|*.*") as Avalonia's file types.</summary>
        internal static List<FilePickerFileType> Filters(string filter)
        {
            var parts = filter.Split('|');
            var types = new List<FilePickerFileType>();
            for (var i = 0; i + 1 < parts.Length; i += 2)
            {
                types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries) });
            }

            return types;
        }
    }

    /// <summary>The current theme's colours from the app's resources; anything missing falls back to the dark palette's.</summary>
    private sealed class AvaloniaGraphTheme : IGraphTheme
    {
        public GraphPalette Palette
        {
            get
            {
                var app = Application.Current;
                var fallback = GraphPalette.Dark;
                if (app is null)
                {
                    return fallback;
                }

                string Hex(string key, string otherwise)
                    => app.TryGetResource(key, app.ActualThemeVariant, out var value) switch
                    {
                        true when value is Color c => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
                        true when value is ISolidColorBrush b => $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}",
                        _ => otherwise,
                    };

                return fallback with
                {
                    Text = Hex("TextSecondaryColor", fallback.Text),
                    Grid = Hex("BorderColor", fallback.Grid),
                    Accent = Hex("AccentBrush", fallback.Accent),
                    Ok = Hex("OkColor", fallback.Ok),
                    Warning = Hex("WarningColor", fallback.Warning),
                    Critical = Hex("CriticalColor", fallback.Critical),
                };
            }
        }
    }

    private sealed class AvaloniaThemeInfo : IAppThemeInfo
    {
        public AppTheme Effective => ThemeSetup.Effective(Application.Current);

        public bool SystemUsesLightTheme()
            => Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Light;
    }
}
