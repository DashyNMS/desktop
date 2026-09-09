using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopNMS.Core.Models;

/// <summary>Version information returned by /api/v0/system.</summary>
public sealed class SystemInfo
{
    [JsonPropertyName("local_ver")]
    public string? LocalVersion { get; set; }

    [JsonPropertyName("local_sha")]
    public string? LocalCommit { get; set; }

    [JsonPropertyName("local_date")]
    public string? LocalDate { get; set; }

    [JsonPropertyName("local_branch")]
    public string? LocalBranch { get; set; }

    [JsonPropertyName("db_schema")]
    public string? DatabaseSchema { get; set; }

    [JsonPropertyName("php_ver")]
    public string? PhpVersion { get; set; }

    [JsonPropertyName("python_ver")]
    public string? PythonVersion { get; set; }

    [JsonPropertyName("database_ver")]
    public string? DatabaseVersion { get; set; }

    [JsonPropertyName("rrdtool_ver")]
    public string? RrdToolVersion { get; set; }

    [JsonPropertyName("netsnmp_ver")]
    public string? NetSnmpVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(LocalVersion) ? "LibreNMS" : $"LibreNMS {LocalVersion}";
}
