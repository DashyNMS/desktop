using System;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Help;

namespace DesktopNMS.Services.Help;

/// <summary>
/// The user guide built into the app (#604): docs/guide and the screenshots
/// it shows, exactly as they were for this version, so help works offline and
/// on first run.
/// </summary>
public static class BundledHelp
{
    private const string ResourcePrefix = "DesktopNMS.Help/";

    private static readonly Lazy<HelpFileSet> LazyFiles = new(() => HelpFileSet.FromAssembly(typeof(BundledHelp).Assembly, ResourcePrefix));

    public static HelpFileSet Files => LazyFiles.Value;

    private static readonly Lazy<HelpGuide> LazyGuide = new(() => HelpGuide.Load(HelpProduct.Desktop, null, Files));

    /// <summary>Whether this version's guide has a page, so an address for one it doesn't have still opens on the web.</summary>
    public static bool HasPage(string pageId) => LazyGuide.Value.Find(pageId) is not null;

    /// <summary>The guide to show for the running version. Downloaded copies go with the rest of DashyNMS's cache.</summary>
    public static HelpGuideService CreateService(string runningVersion)
        => new(HelpProduct.Desktop, HelpRelease.ForVersion(runningVersion), Files, AppPaths.HelpCacheDirectory);
}
