using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Services.Intelligence;

public sealed partial class McpToolHost
{
    /// <summary>
    /// The MCP server's settings (see the MCP README's table), from this app's
    /// own connection and settings, read once into a list so the server and its
    /// identity come from the same snapshot.
    /// </summary>
    /// <exception cref="IntelligenceException">A saved setting the server would refuse.</exception>
    internal static List<KeyValuePair<string, string>> ToolSettings(
        LibreNmsConnection connection, AppSettings settings, Func<string?> unimusToken, Func<string?> graylogPassword)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("LIBRENMS_URL", connection.WebRoot.ToString()),
            new("LIBRENMS_TOKEN", connection.ApiToken),
            new("LIBRENMS_TIMEOUT", Math.Max(5, connection.TimeoutSeconds).ToString(CultureInfo.InvariantCulture)),
            new("LIBRENMS_TIMESTAMPS_UTC", settings.ServerTimestampsAreUtc ? "true" : "false"),
        };
        if (connection.BackupWebRoot is { } backup)
        {
            values.Add(new("LIBRENMS_BACKUP_URL", backup.ToString()));
        }

        if (connection.TrustedCertificates.Count > 0)
        {
            values.Add(new("LIBRENMS_TRUSTED_CERTS", string.Join(",", connection.TrustedCertificates)));
        }

        // Settings › Intelligence: full (redacted) config text, a summary
        // ("did it change, when and by how much"), or nothing from Unimus.
        values.Add(new("MCP_CONFIG_TEXT", ConfigText(settings)));

        if (settings.Unimus is { Enabled: true, Url: { Length: > 0 } unimusUrl } unimus && unimusToken() is { Length: > 0 } token)
        {
            values.Add(new("UNIMUS_URL", unimusUrl));
            values.Add(new("UNIMUS_TOKEN", token));
            if (unimus.TrustedCertificates.Count > 0)
            {
                values.Add(new("UNIMUS_TRUSTED_CERTS", string.Join(",", unimus.TrustedCertificates)));
            }
        }

        if (settings.Graylog is { Enabled: true } graylog)
        {
            var password = graylogPassword();
            if (GraylogConnection.FromSettings(graylog, password, out _) is { } graylogConnection)
            {
                values.Add(new("GRAYLOG_URL", graylogConnection.Root.ToString()));
                values.Add(new("GRAYLOG_USERNAME", graylog.Username ?? string.Empty));
                values.Add(new("GRAYLOG_PASSWORD", password ?? string.Empty));
                values.Add(new("GRAYLOG_VERSION", graylog.Version == GraylogSettings.Version20 ? "2.0" : "2.1"));
                values.Add(new("GRAYLOG_QUERY_FIELD", graylog.QueryField));
                values.Add(new("GRAYLOG_MATCH_ANY_ADDRESS", graylog.MatchAnyAddress ? "true" : "false"));
                if (graylog.TrustedCertificates.Count > 0)
                {
                    values.Add(new("GRAYLOG_TRUSTED_CERTS", string.Join(",", graylog.TrustedCertificates)));
                }
            }
        }

        values.AddRange(SensorAssessment(settings));
        return values;
    }

    /// <summary>
    /// Settings › Health thresholds, so the tools judge sensors as the Health
    /// tab does. Every bound is sent, defaults included, with the override
    /// choice stated either way; the server checks the same ordering, but a
    /// refusal there would only be a failed start, so it's checked here first.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string>> SensorAssessment(AppSettings settings)
    {
        var dbm = settings.DbmThresholds;
        var signal = settings.SignalThresholds;
        var temperature = settings.TemperatureThresholds;
        var fan = settings.FanSpeedThresholds;
        Check("dBm sensors", [dbm.WarningThreshold, dbm.CriticalThreshold, dbm.IgnoreAtOrAbove, dbm.IgnoreAtOrBelow],
            dbm.IgnoreAtOrBelow < dbm.CriticalThreshold && dbm.CriticalThreshold <= dbm.WarningThreshold && dbm.WarningThreshold < dbm.IgnoreAtOrAbove);
        Check("Signal sensors", [signal.WarningThreshold, signal.CriticalThreshold, signal.IgnoreAtOrAbove, signal.IgnoreAtOrBelow],
            signal.IgnoreAtOrBelow < signal.CriticalThreshold && signal.CriticalThreshold <= signal.WarningThreshold && signal.WarningThreshold < signal.IgnoreAtOrAbove);
        Check("Temperature sensors", [temperature.LowCritical, temperature.LowWarning, temperature.HighWarning, temperature.HighCritical],
            temperature.LowCritical <= temperature.LowWarning && temperature.LowWarning <= temperature.HighWarning && temperature.HighWarning <= temperature.HighCritical);
        Check("Fan speed sensors", [fan.LowCritical, fan.LowWarning, fan.HighWarning, fan.HighCritical],
            fan.LowCritical <= fan.LowWarning && fan.LowWarning <= fan.HighWarning && fan.HighWarning <= fan.HighCritical);

        return
        [
            Number("MCP_DBM_WARNING_THRESHOLD", dbm.WarningThreshold),
            Number("MCP_DBM_CRITICAL_THRESHOLD", dbm.CriticalThreshold),
            Number("MCP_DBM_IGNORE_AT_OR_ABOVE", dbm.IgnoreAtOrAbove),
            Number("MCP_DBM_IGNORE_AT_OR_BELOW", dbm.IgnoreAtOrBelow),
            Number("MCP_SIGNAL_WARNING_THRESHOLD", signal.WarningThreshold),
            Number("MCP_SIGNAL_CRITICAL_THRESHOLD", signal.CriticalThreshold),
            Number("MCP_SIGNAL_IGNORE_AT_OR_ABOVE", signal.IgnoreAtOrAbove),
            Number("MCP_SIGNAL_IGNORE_AT_OR_BELOW", signal.IgnoreAtOrBelow),
            Number("MCP_TEMPERATURE_LOW_CRITICAL", temperature.LowCritical),
            Number("MCP_TEMPERATURE_LOW_WARNING", temperature.LowWarning),
            Number("MCP_TEMPERATURE_HIGH_WARNING", temperature.HighWarning),
            Number("MCP_TEMPERATURE_HIGH_CRITICAL", temperature.HighCritical),
            Number("MCP_FAN_SPEED_LOW_CRITICAL", fan.LowCritical),
            Number("MCP_FAN_SPEED_LOW_WARNING", fan.LowWarning),
            Number("MCP_FAN_SPEED_HIGH_WARNING", fan.HighWarning),
            Number("MCP_FAN_SPEED_HIGH_CRITICAL", fan.HighCritical),
            new("MCP_OVERRIDE_SENSOR_LIMITS", settings.OverrideSensorLimitsWithAppThresholds ? "true" : "false"),
        ];

        // Names the section only: the values themselves stay out of messages and logs.
        static void Check(string section, double[] bounds, bool ordered)
        {
            if (!bounds.All(double.IsFinite) || !ordered)
            {
                throw new IntelligenceException(
                    $"Intelligence can't judge sensors until the {section} thresholds in Settings › Health thresholds are numbers in order. Correct them, then ask again.",
                    IntelligenceFailure.Tools);
            }
        }

        // Round-trip and culture-free, so a German "1,5" never reaches the server.
        static KeyValuePair<string, string> Number(string key, double value) => new(key, value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Which server a set of settings needs: a digest of every value, kept in
    /// memory only, so any change - account, certificate, a credential, a
    /// threshold - means a new server and never a leaked secret.
    /// </summary>
    internal static string Identity(IEnumerable<KeyValuePair<string, string>> settings)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(key).Append('\0').Append(value).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The MCP server's MCP_CONFIG_TEXT - summary unless set to one of the others.</summary>
    private static string ConfigText(AppSettings settings) => settings.IntelligenceConfigText switch
    {
        AppSettings.IntelligenceConfigTextFull => "full",
        AppSettings.IntelligenceConfigTextOff => "off",
        _ => "summary",
    };
}
