using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One line group from <c>backups/diff</c>'s <c>lineGroups</c> array. Unimus's
/// own vocabulary for <see cref="Type"/> (confirmed against its API v2 docs):
/// <c>COMMON</c> (unchanged - present in both), <c>CHANGED</c> (present in
/// both, differs - <see cref="OriginalLines"/>/<see cref="RevisedLines"/> line
/// up), <c>INSERTED</c> (only in <see cref="RevisedLines"/>), <c>DELETED</c>
/// (only in <see cref="OriginalLines"/>).
/// </summary>
public sealed class UnimusDiffLineGroup
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "COMMON";

    [JsonPropertyName("originalLines")]
    public List<UnimusDiffLine> OriginalLines { get; set; } = new();

    [JsonPropertyName("revisedLines")]
    public List<UnimusDiffLine> RevisedLines { get; set; } = new();

    [JsonIgnore]
    public bool IsCommon => string.Equals(Type, "COMMON", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsChanged => string.Equals(Type, "CHANGED", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsInserted => string.Equals(Type, "INSERTED", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsDeleted => string.Equals(Type, "DELETED", StringComparison.OrdinalIgnoreCase);
}

public sealed class UnimusDiffLine
{
    /// <summary>1-based line number in whichever backup this line belongs to; null for a line Unimus doesn't number.</summary>
    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>The unified diff between two backups - <c>GET backups/diff?origId=&amp;revId=</c>'s <c>data</c> object.</summary>
public sealed class UnimusBackupDiff
{
    [JsonPropertyName("lineGroups")]
    public List<UnimusDiffLineGroup> LineGroups { get; set; } = new();
}
