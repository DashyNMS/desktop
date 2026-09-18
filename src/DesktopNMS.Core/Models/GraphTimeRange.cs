namespace DesktopNMS.Core.Models;

/// <summary>The five presets the shared time-range picker offers, plus an explicit custom range (issue #13).</summary>
public enum GraphTimeRangePreset
{
    Hour,
    Day,
    Week,
    Month,
    Year,
    Custom,
}

/// <summary>
/// A graph's requested time window - reused by every graph-hosting view
/// (issue #13), and used as-is as part of the per-window graph cache's key
/// (issue #20), since it already has correct value equality via being a
/// record.
/// </summary>
public sealed record GraphTimeRange(GraphTimeRangePreset Preset, DateTime? CustomFrom = null, DateTime? CustomTo = null)
{
    public static readonly GraphTimeRange LastHour = new(GraphTimeRangePreset.Hour);
    public static readonly GraphTimeRange LastDay = new(GraphTimeRangePreset.Day);
    public static readonly GraphTimeRange LastWeek = new(GraphTimeRangePreset.Week);
    public static readonly GraphTimeRange LastMonth = new(GraphTimeRangePreset.Month);
    public static readonly GraphTimeRange LastYear = new(GraphTimeRangePreset.Year);

    public static GraphTimeRange Custom(DateTime from, DateTime to) => new(GraphTimeRangePreset.Custom, from, to);

    /// <summary>
    /// The graph endpoint's "from" query value - LibreNMS/RRDtool's own
    /// relative-time shorthand for a preset (confirmed live: "-1week" is
    /// honoured and changes the rendered range), or a Unix timestamp for a
    /// custom range.
    /// </summary>
    public string ToFromParameter() => Preset switch
    {
        GraphTimeRangePreset.Hour => "-1hour",
        GraphTimeRangePreset.Day => "-1day",
        GraphTimeRangePreset.Week => "-1week",
        GraphTimeRangePreset.Month => "-1month",
        GraphTimeRangePreset.Year => "-1year",
        GraphTimeRangePreset.Custom => ToUnixSeconds(CustomFrom ?? DateTime.UtcNow.AddDays(-1)),
        _ => "-1day",
    };

    /// <summary>The "to" query value - omitted for a preset (the server defaults to "now"), a Unix timestamp for a custom range.</summary>
    public string? ToToParameter() => Preset == GraphTimeRangePreset.Custom
        ? ToUnixSeconds(CustomTo ?? DateTime.UtcNow)
        : null;

    private static string ToUnixSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();
}
