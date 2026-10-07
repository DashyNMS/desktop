using System;
using System.Collections.Generic;
using System.Linq;
using DesktopNMS.Core.Licences;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// Everything third-party desktop ships (#266), for Settings › About's
/// open-source licences window: Core's shared entries (.NET, the fonts,
/// OpenStreetMap) and desktop's own. The licence texts are Core's.
/// </summary>
/// <remarks>
/// Kept by hand: a new NuGet package or bundled asset gets a line here, and
/// its licence text in Core's Licences/Texts. Microsoft's System.* and
/// Microsoft.Extensions.* packages come under ".NET". Test-only packages and
/// the Inno Setup installer (whose licence asks for no notice) aren't listed.
/// </remarks>
public static class DesktopOpenSourceNotices
{
    private static readonly OpenSourceNotice[] Own =
    [
        new("WPF and Windows Forms", "The app's windows", OpenSourceNotices.DotNetFoundation, "MIT",
            new Uri("https://github.com/dotnet/wpf"), OpenSourceNotices.Mit, NoticePlatforms.Windows),
        new("Windows Community Toolkit Notifications", "Alert notifications", OpenSourceNotices.DotNetFoundation, "MIT",
            new Uri("https://github.com/CommunityToolkit/WindowsCommunityToolkit"), OpenSourceNotices.Mit, NoticePlatforms.Windows),
        new("SharpVectors", "Drawing LibreNMS's graphs", "Copyright (c) 2010 - 2026, Elinam LLC", "BSD 3-Clause",
            new Uri("https://github.com/ElinamLLC/SharpVectors"), OpenSourceNotices.Bsd3SharpVectors, NoticePlatforms.Windows),
        new("Microsoft Edge WebView2 SDK", "Sign in with LibreNMS", "Copyright (C) Microsoft Corporation", "BSD 3-Clause",
            new Uri("https://aka.ms/webview2"), OpenSourceNotices.Bsd3WebView2, NoticePlatforms.Windows),
    ];

    /// <summary>In order: the platform first (.NET, WPF), then libraries, fonts and map data.</summary>
    public static IReadOnlyList<OpenSourceNotice> All { get; } =
        OpenSourceNotices.For(NoticePlatforms.Windows, OpenSourceNotices.Shared.Take(1).Concat(Own).Concat(OpenSourceNotices.Shared.Skip(1)));
}
