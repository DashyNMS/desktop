using DesktopNMS.Core.Configuration;

namespace DesktopNMS.Diagnostics;

public static class DiagnosticsConsent
{
    public const int CurrentVersion = 2;
    public static bool Errors(AppSettings settings) => settings.DiagnosticsSharing is
        DiagnosticsSharingPreference.ErrorsOnly or DiagnosticsSharingPreference.ErrorsAndUsage;
    public static bool Launches(AppSettings settings) => settings.DiagnosticsSharing is
        DiagnosticsSharingPreference.UsageOnly or DiagnosticsSharingPreference.ErrorsAndUsage;
    public static bool Metrics(AppSettings settings) => Launches(settings) && settings.DiagnosticsConsentVersion is 1 or CurrentVersion;
    public static bool Sessions(AppSettings settings) => Metrics(settings) && Errors(settings) &&
        settings.DiagnosticsConsentVersion == CurrentVersion;
    public static bool NeedsChoice(AppSettings settings) => !Enum.IsDefined(settings.DiagnosticsSharing) ||
        settings.DiagnosticsSharing == DiagnosticsSharingPreference.Undecided ||
        (Launches(settings) && settings.DiagnosticsConsentVersion != CurrentVersion);

    public static bool ChangeUsageChoice(AppSettings settings, bool enabled)
    {
        // A two-way binding can write its initial value back without a user action.
        if (enabled == Launches(settings)) return false;
        var errors = Errors(settings);
        settings.DiagnosticsConsentVersion = CurrentVersion;
        settings.DiagnosticsSharing = (errors, enabled) switch
        {
            (true, true) => DiagnosticsSharingPreference.ErrorsAndUsage,
            (true, false) => DiagnosticsSharingPreference.ErrorsOnly,
            (false, true) => DiagnosticsSharingPreference.UsageOnly,
            _ => DiagnosticsSharingPreference.None,
        };
        return true;
    }
}
