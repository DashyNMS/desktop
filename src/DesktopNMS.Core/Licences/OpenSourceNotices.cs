namespace DesktopNMS.Core.Licences;

/// <summary>Which apps' builds ship a component.</summary>
[Flags]
public enum NoticePlatforms
{
    Windows = 1,
    iOS = 2,
    Android = 4,
    All = Windows | iOS | Android,
}

/// <summary>
/// One third-party component an app ships, and the notice its licence asks to
/// go out with it (#266, after DashyNMS Mobile's #164).
/// </summary>
/// <param name="Name">What it's called: "Leaflet".</param>
/// <param name="Use">What the app uses it for: "The map".</param>
/// <param name="Copyright">Its copyright line, as its licence gives it.</param>
/// <param name="Licence">The licence's name: "MIT".</param>
/// <param name="Link">Its home page, or for map data the copyright page.</param>
/// <param name="LicenceFile">
/// The licence's full text, one of <see cref="OpenSourceNotices.LicenceFiles"/> -
/// or null when the notice itself is what's asked for (OpenStreetMap's attribution).
/// </param>
/// <param name="Platforms">Which apps ship it - AndroidX only on Android, WebView2 only on Windows.</param>
public sealed record OpenSourceNotice(
    string Name,
    string Use,
    string Copyright,
    string Licence,
    Uri Link,
    string? LicenceFile,
    NoticePlatforms Platforms = NoticePlatforms.All);

/// <summary>
/// The parts of the open-source licences both apps share (#266): the notice
/// type, the entries for what Core itself brings, every licence text either
/// app needs, and the trademark line. Each app adds its own list - what each
/// ships differs - and shows them in About.
/// </summary>
/// <remarks>
/// Kept by hand: a new package, bundled script or font in either app gets a
/// line in that app's list, and its licence text here under Licences/Texts
/// (embedded, so it ships inside the app however it's installed). A test
/// checks every file a list names is here.
/// </remarks>
public static class OpenSourceNotices
{
    /// <summary>The trademark line in About - the website and READMEs say the same of LibreNMS.</summary>
    public const string Trademarks =
        "DashyNMS isn't affiliated with LibreNMS or Graylog. LibreNMS and Graylog are trademarks of their respective owners.";

    public const string DotNetFoundation = "Copyright (c) .NET Foundation and Contributors";

    // Licence texts, by file name.
    public const string Mit = "mit.txt";
    public const string Apache2 = "apache-2.0.txt";
    public const string Bsd2Leaflet = "bsd-2-clause-leaflet.txt";
    public const string Bsd3SharpVectors = "bsd-3-clause-sharpvectors.txt";
    public const string Bsd3WebView2 = "bsd-3-clause-webview2.txt";
    public const string OflIbmPlex = "ofl-ibm-plex.txt";
    public const string OflSora = "ofl-sora.txt";

    private const string ResourcePrefix = "DesktopNMS.Core.Licences.";

    /// <summary>
    /// What Core itself brings into both apps: .NET's libraries, including the
    /// Microsoft.Extensions packages Core references - and the fonts and map
    /// data both apps use, whose notices read the same in each.
    /// </summary>
    public static IReadOnlyList<OpenSourceNotice> Shared { get; } =
    [
        new(".NET", "The runtime and libraries the app is built on, including Microsoft.Extensions", DotNetFoundation, "MIT",
            new Uri("https://github.com/dotnet/runtime"), Mit),
        new("IBM Plex Sans and Mono", "The app's text and figures", "Copyright © 2017 IBM Corp. with Reserved Font Name \"Plex\"", "SIL Open Font License 1.1",
            new Uri("https://github.com/IBM/plex"), OflIbmPlex),
        new("Sora", "The app's headings", "Copyright 2019 The Sora Project Authors", "SIL Open Font License 1.1",
            new Uri("https://github.com/sora-xor/sora-font"), OflSora),
        new("OpenStreetMap", "The map's tiles and data", "© OpenStreetMap contributors", "Open Database License (ODbL)",
            new Uri("https://www.openstreetmap.org/copyright"), null),
    ];

    /// <summary>Every licence text shipped in Core, by file name.</summary>
    public static IReadOnlyList<string> LicenceFiles { get; } =
        typeof(OpenSourceNotices).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>A licence's full text, or null for a file Core doesn't have.</summary>
    public static string? ReadLicence(string licenceFile)
    {
        using var stream = typeof(OpenSourceNotices).Assembly.GetManifestResourceStream(ResourcePrefix + licenceFile);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The entries one platform's build ships, in order.</summary>
    public static IReadOnlyList<OpenSourceNotice> For(NoticePlatforms platform, IEnumerable<OpenSourceNotice> notices)
        => notices.Where(n => (n.Platforms & platform) != 0).ToList();
}
