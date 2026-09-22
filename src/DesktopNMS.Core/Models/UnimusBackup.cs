using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>
/// One backup revision from Unimus - a row from <c>devices/{id}/backups</c> or
/// <c>devices/{id}/backups/latest</c>. <see cref="Bytes"/> is base64, and only
/// meaningful when <see cref="Type"/> is "TEXT"; Unimus can also store BINARY
/// backups, which it says up front (in <see cref="Api.IUnimusApi"/>'s own
/// docs) it has no way to render - this app doesn't attempt to either.
/// </summary>
public sealed class UnimusBackup
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Unix seconds - Unimus's own epoch, unrelated to LibreNMS's server-local timestamp convention. See <see cref="ValidSinceUtc"/>.</summary>
    [JsonPropertyName("validSince")]
    public long? ValidSince { get; set; }

    [JsonPropertyName("validUntil")]
    public long? ValidUntil { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "TEXT";

    /// <summary>Base64 content, present only when the caller asked for it (see <see cref="Api.IUnimusApi.GetLatestBackupAsync"/> vs the list endpoints, which omit it).</summary>
    [JsonPropertyName("bytes")]
    public string? Bytes { get; set; }

    [JsonIgnore]
    public DateTime? ValidSinceUtc => ValidSince is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;

    [JsonIgnore]
    public DateTime? ValidUntilUtc => ValidUntil is { } u ? DateTimeOffset.FromUnixTimeSeconds(u).UtcDateTime : null;

    [JsonIgnore]
    public bool IsText => string.Equals(Type, "TEXT", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decoded text content, or null for a BINARY backup or one fetched without content.</summary>
    [JsonIgnore]
    public string? Content => IsText && !string.IsNullOrEmpty(Bytes) && TryDecode(Bytes, out var text) ? text : null;

    private static bool TryDecode(string base64, out string text)
    {
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
    }
}

/// <summary>One page of a device's backup history - <c>devices/{id}/backups</c>'s envelope.</summary>
public sealed class UnimusBackupPage
{
    public IReadOnlyList<UnimusBackup> Backups { get; init; } = new List<UnimusBackup>();

    public int TotalCount { get; init; }

    public int TotalPages { get; init; }

    public int Page { get; init; }
}
