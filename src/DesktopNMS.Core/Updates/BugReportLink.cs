namespace DesktopNMS.Core.Updates;

/// <summary>
/// The "Report a bug" link in Settings, About (#149): a new GitHub issue with
/// the details every bug report needs already filled in - the app, Windows
/// and LibreNMS versions, and nothing else (no server address, token or
/// device names). Nothing is sent until the user submits the issue on
/// GitHub's own page, where they see and can edit all of it first.
/// </summary>
public static class BugReportLink
{
    public const string NewIssueUrl = "https://github.com/DashyNMS/desktop/issues/new";

    public static Uri Build(string appVersion, string windowsVersion, string? libreNmsVersion)
    {
        var body =
            "**What happened?**\n\n\n" +
            "**What did you expect to happen?**\n\n\n" +
            "**Steps to reproduce**\n1. \n\n" +
            "---\n" +
            $"DashyNMS: {Clean(appVersion)}\n" +
            $"Windows: {Clean(windowsVersion)}\n" +
            $"LibreNMS: {(string.IsNullOrWhiteSpace(libreNmsVersion) || libreNmsVersion == "-" ? "not connected" : Clean(libreNmsVersion))}\n";

        return new Uri($"{NewIssueUrl}?labels=bug&body={Uri.EscapeDataString(body)}");
    }

    private static string Clean(string value) => value.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
