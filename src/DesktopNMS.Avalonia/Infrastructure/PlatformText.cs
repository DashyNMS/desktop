namespace DesktopNMS.Infrastructure;

/// <summary>Words that differ by OS, for the views (#250) - the WPF app's text says "your Windows account".</summary>
public static class PlatformText
{
    /// <summary>"…stored encrypted for {this}" - where #154 keeps secrets.</summary>
    public static string SecretsKeptFor { get; } =
        OperatingSystem.IsWindows() ? "your Windows account only"
        : OperatingSystem.IsMacOS() ? "you only, with a key in your Keychain"
        : "you only, with a key in your keyring";

    /// <summary>"this PC", "this Mac", "this computer".</summary>
    public static string ThisComputer { get; } =
        OperatingSystem.IsWindows() ? "this PC"
        : OperatingSystem.IsMacOS() ? "this Mac"
        : "this computer";
}
