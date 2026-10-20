using DesktopNMS.Core.Graphs;

namespace DesktopNMS.Infrastructure;

/// <summary>Putting text on the clipboard.</summary>
public interface IClipboard
{
    /// <summary>False when another process briefly holds the clipboard; most callers can ignore it.</summary>
    bool SetText(string text);
}

/// <summary>
/// What a file dialog asks for. <see cref="Filter"/> uses the familiar
/// "CSV files (*.csv)|*.csv|All files (*.*)|*.*" form; a front end whose
/// dialogs take something else translates it.
/// </summary>
public sealed record FileDialogOptions(string Filter, string? FileName = null, string? Title = null, string? DefaultExtension = null);

/// <summary>The front end's open and save dialogs. Each returns the chosen path, or null if cancelled.</summary>
public interface IFileDialogs
{
    string? PickFileToOpen(FileDialogOptions options);

    string? PickFileToSave(FileDialogOptions options);
}

/// <summary>The current theme's graph colours - text, grid, accent and severities.</summary>
public interface IGraphTheme
{
    GraphPalette Palette { get; }
}

/// <summary>The palette in use and Windows' (or the OS's) own light/dark choice.</summary>
public interface IAppThemeInfo
{
    /// <summary>Dark or Light - the palette the app started with; never "match the system".</summary>
    DesktopNMS.Core.Configuration.AppTheme Effective { get; }

    /// <summary>The operating system's app mode is light. True (light) when it can't be read.</summary>
    bool SystemUsesLightTheme();
}

/// <summary>Which modifier keys are held - for a click that means something different with Ctrl held.</summary>
public interface IKeyboardState
{
    bool IsControlDown { get; }
}

/// <summary>
/// The front end's clipboard, file dialogs and theme colours, set once at
/// startup (#249) - alongside <see cref="UiThread"/>. Until then the
/// clipboard does nothing, dialogs are cancelled and graphs use the dark
/// palette, which keeps tests and design-time use quiet.
/// </summary>
public static class UiPlatform
{
    public static IClipboard Clipboard { get; private set; } = new NoClipboard();

    public static IFileDialogs FileDialogs { get; private set; } = new NoFileDialogs();

    public static IGraphTheme GraphTheme { get; private set; } = new DarkGraphTheme();

    public static IAppThemeInfo Theme { get; private set; } = new DarkTheme();

    public static IKeyboardState Keyboard { get; private set; } = new NoKeys();

    /// <summary>Called once by the front end at startup.</summary>
    public static void Use(IClipboard clipboard, IFileDialogs fileDialogs, IGraphTheme graphTheme, IAppThemeInfo theme, IKeyboardState keyboard)
    {
        Clipboard = clipboard;
        FileDialogs = fileDialogs;
        GraphTheme = graphTheme;
        Theme = theme;
        Keyboard = keyboard;
    }

    private sealed class DarkTheme : IAppThemeInfo
    {
        public DesktopNMS.Core.Configuration.AppTheme Effective => DesktopNMS.Core.Configuration.AppTheme.Dark;

        public bool SystemUsesLightTheme() => true;
    }

    private sealed class NoKeys : IKeyboardState
    {
        public bool IsControlDown => false;
    }

    private sealed class NoClipboard : IClipboard
    {
        public bool SetText(string text) => false;
    }

    private sealed class NoFileDialogs : IFileDialogs
    {
        public string? PickFileToOpen(FileDialogOptions options) => null;

        public string? PickFileToSave(FileDialogOptions options) => null;
    }

    private sealed class DarkGraphTheme : IGraphTheme
    {
        public GraphPalette Palette => GraphPalette.Dark;
    }
}
