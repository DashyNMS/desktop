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
        new("Avalonia", "The app's windows on Windows, macOS and Linux", "Copyright (c) AvaloniaUI OÜ", "MIT",
            new Uri("https://github.com/AvaloniaUI/Avalonia"), OpenSourceNotices.Mit),
        new("FluentIcons", "The app's icons", "Copyright (c) 2022 davidxuang; Fluent System Icons copyright (c) Microsoft Corporation", "MIT",
            new Uri("https://github.com/davidxuang/FluentIcons"), OpenSourceNotices.Mit),
        new("SVG.Skia", "Drawing LibreNMS's graphs", "Copyright (c) Wiesław Šoltés", "MIT",
            new Uri("https://github.com/wieslawsoltes/Svg.Skia"), OpenSourceNotices.Mit),
        new("SkiaSharp and HarfBuzzSharp", "Graphics and text rendering", "Copyright (c) 2015-2016 Xamarin, Inc.; copyright (c) 2017-2018 Microsoft Corporation", "MIT",
            new Uri("https://github.com/mono/SkiaSharp"), OpenSourceNotices.Mit),
        new("Windows Community Toolkit Notifications", "Alert notifications", OpenSourceNotices.DotNetFoundation, "MIT",
            new Uri("https://github.com/CommunityToolkit/WindowsCommunityToolkit"), OpenSourceNotices.Mit, NoticePlatforms.Windows),
    ];

    /// <summary>In order: the runtime, UI libraries, fonts and map data.</summary>
    public static IReadOnlyList<OpenSourceNotice> All { get; } =
        OpenSourceNotices.Shared.Take(1).Concat(Own).Concat(OpenSourceNotices.Shared.Skip(1))
            .Where(notice => notice.Platforms != NoticePlatforms.Windows || OperatingSystem.IsWindows()).ToList();
}
