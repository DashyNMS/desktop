using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Models;

/// <summary>
/// Works out which sensors describe the same physical thing, from their
/// descriptions alone.
/// </summary>
/// <remarks>
/// LibreNMS names a sensor for the component it sits on and the quantity it
/// measures, in that order - "1/1/15 Lane 1 RX Power", "Disk 9 Temperature",
/// "PSU 1 Voltage". The component part almost always ends with the number
/// that identifies which one it is, so the leading tokens up to and including
/// the first one carrying a digit are the component, and the rest is the
/// measurement.
///
/// Keeping the number in the key is what makes this safe: "Fan 1" and "Fan 2"
/// stay apart, where grouping on the word "Fan" alone would wrongly merge
/// every fan on the device.
///
/// Sensors whose description has no number at all ("CPU Temperature") have
/// nothing to correlate on, so they group only with an identical description.
/// </remarks>
public static class SensorGrouping
{
    /// <summary>
    /// Leading non-numeric words (bounded, so a long description cannot
    /// produce an absurd key), then the first word carrying a digit.
    /// </summary>
    private static readonly Regex ComponentPrefixPattern =
        new(@"^((?:[^\s\d]+\s+){0,4}\S*\d\S*)", RegexOptions.Compiled);

    /// <summary>
    /// The key sensors on the same component share, or the whole description
    /// when it carries no identifying number.
    /// </summary>
    public static string ExtractGroupKey(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var trimmed = description.Trim();
        var match = ComponentPrefixPattern.Match(trimmed);

        return match.Success ? match.Groups[1].Value.TrimEnd() : trimmed;
    }

    /// <summary>
    /// What is left of <paramref name="description"/> once the group's shared
    /// key is taken off the front - the measurement on its own, e.g. "Lane 1
    /// RX Power". Empty when the description is nothing but the key, which is
    /// the case for a group of identically-named sensors.
    /// </summary>
    public static string ExtractMeasurementLabel(string? description, string groupKey)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var trimmed = description.Trim();

        if (groupKey.Length >= trimmed.Length || !trimmed.StartsWith(groupKey, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed[groupKey.Length..].TrimStart(' ', '-', ':', ',');
    }
}
