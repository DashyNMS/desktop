using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Core.Devices;

/// <summary>
/// One device to bulk add: its hostname plus any per-device settings from a
/// CSV row (keyed by the LibreNMS add_device field name), or why the row
/// can't be added.
/// </summary>
public sealed record BulkDeviceRow(int Line, string Hostname, IReadOnlyDictionary<string, string> Values, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>The rows from a paste or a CSV, plus anything about the input as a whole worth saying (a missing column, ignored columns).</summary>
public sealed record BulkDeviceParseResult(IReadOnlyList<BulkDeviceRow> Rows, IReadOnlyList<string> Messages)
{
    public static BulkDeviceParseResult Empty { get; } = new(Array.Empty<BulkDeviceRow>(), Array.Empty<string>());
}

/// <summary>
/// Turns a pasted list or a CSV file into devices to add, and each of those
/// into a POST /devices request on top of the dialog's shared settings.
/// Checked the way LibreNMS's own <c>add_device</c> checks
/// (<c>includes/html/api_functions.inc.php</c>), so a row that LibreNMS
/// would reject is flagged before anything is sent.
/// </summary>
public static class BulkDeviceImport
{
    // LibreNMS's add_device field names - also the CSV column names.
    public const string Hostname = "hostname";
    public const string SnmpVersion = "snmpver";
    public const string Community = "community";
    public const string AuthLevel = "authlevel";
    public const string AuthName = "authname";
    public const string AuthPass = "authpass";
    public const string AuthAlgo = "authalgo";
    public const string CryptoPass = "cryptopass";
    public const string CryptoAlgo = "cryptoalgo";
    public const string Port = "port";
    public const string Transport = "transport";
    public const string PollerGroup = "poller_group";
    public const string ForceAdd = "force_add";
    public const string PingFallback = "ping_fallback";
    public const string SnmpDisable = "snmp_disable";
    public const string Os = "os";
    public const string SysName = "sysName";
    public const string Hardware = "hardware";
    public const string Location = "location";
    public const string DisplayTemplate = "display_template";
    public const string OverwriteIp = "overwrite_ip";

    /// <summary>Every column a CSV may have, in the template's order.</summary>
    public static IReadOnlyList<string> Columns { get; } = new[]
    {
        Hostname, SnmpVersion, Community, AuthLevel, AuthName, AuthPass, AuthAlgo, CryptoPass, CryptoAlgo,
        Port, Transport, PollerGroup, Location, DisplayTemplate, OverwriteIp, ForceAdd, PingFallback,
        SnmpDisable, Os, SysName, Hardware,
    };

    /// <summary>Other names a column may go by - LibreNMS's legacy "version", and the obvious ones for the hostname.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["version"] = SnmpVersion,
        ["snmp_version"] = SnmpVersion,
        ["host"] = Hostname,
        ["ip"] = Hostname,
        ["address"] = Hostname,
        ["device"] = Hostname,
    };

    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal) { ForceAdd, PingFallback, SnmpDisable };

    // LibreNMS's Validate::hostname, pattern for pattern.
    private static readonly Regex HostnameChars = new(@"^([a-z\d](-*[a-z\d_])*)(\.([a-z\d](-*[a-z\d_])*))*\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HostnameLength = new(@"^.{1,253}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostnameLabels = new(@"^[^\.]{1,63}(\.[^\.]{1,63})*\.?$", RegexOptions.CultureInvariant);

    /// <summary>A hostname or IP LibreNMS would accept (its <c>Validate::hostname</c> or a valid IP).</summary>
    public static bool IsValidHostnameOrIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        return HostnameChars.IsMatch(value) && HostnameLength.IsMatch(value) && HostnameLabels.IsMatch(value);
    }

    /// <summary>
    /// Hostnames or IPs, one per line (commas, semicolons, tabs and spaces
    /// also separate them, so a pasted spreadsheet column or a comma list
    /// works too). Lines starting with # are comments.
    /// </summary>
    public static BulkDeviceParseResult ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BulkDeviceParseResult.Empty;
        }

        var rows = new List<BulkDeviceRow>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            foreach (var entry in line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                rows.Add(new BulkDeviceRow(i + 1, entry, EmptyValues, HostnameError(entry)));
            }
        }

        return new BulkDeviceParseResult(MarkDuplicates(rows), Array.Empty<string>());
    }

    /// <summary>
    /// A CSV with a header row naming LibreNMS add_device fields (see
    /// <see cref="Columns"/>); only hostname is required. A blank cell uses
    /// the dialog's shared setting.
    /// </summary>
    public static BulkDeviceParseResult ParseCsv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BulkDeviceParseResult(Array.Empty<BulkDeviceRow>(), new[] { "The file is empty." });
        }

        var records = CsvReader.Parse(text);
        if (records.Count == 0)
        {
            return new BulkDeviceParseResult(Array.Empty<BulkDeviceRow>(), new[] { "The file is empty." });
        }

        var messages = new List<string>();
        var header = records[0].Fields.Select(NormaliseColumn).ToList();
        var ignored = new List<string>();

        for (var i = 0; i < header.Count; i++)
        {
            if (header[i] is null && !string.IsNullOrWhiteSpace(records[0].Fields[i]))
            {
                ignored.Add(records[0].Fields[i].Trim());
            }
        }

        var hostnameIndex = header.IndexOf(Hostname);
        if (hostnameIndex < 0)
        {
            return new BulkDeviceParseResult(
                Array.Empty<BulkDeviceRow>(),
                new[] { "The first row must name the columns, including one called hostname. Save the template to see the format." });
        }

        if (ignored.Count > 0)
        {
            messages.Add("Ignored columns this can't use: " + string.Join(", ", ignored) + ".");
        }

        var rows = new List<BulkDeviceRow>();
        foreach (var record in records.Skip(1))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count && i < record.Fields.Count; i++)
            {
                var value = record.Fields[i].Trim();
                if (header[i] is { } column && column != Hostname && value.Length > 0)
                {
                    values[column] = value;
                }
            }

            var hostname = hostnameIndex < record.Fields.Count ? record.Fields[hostnameIndex].Trim() : string.Empty;

            // A row that's all blanks (a trailing ",,,," line from a spreadsheet) isn't a device.
            if (hostname.Length == 0 && values.Count == 0)
            {
                continue;
            }

            rows.Add(new BulkDeviceRow(record.Line, hostname, values, HostnameError(hostname) ?? ValuesError(values)));
        }

        if (rows.Count == 0)
        {
            messages.Add("The file has a header row but no devices.");
        }

        return new BulkDeviceParseResult(MarkDuplicates(rows), messages);
    }

    /// <summary>
    /// The request for one row: the dialog's shared settings, with anything
    /// the row sets on top. Switching SNMP version (or to ping-only) in a row
    /// drops the shared settings that belong to the other kind, so a v3 row
    /// never carries the shared v2c community and vice versa.
    /// </summary>
    public static AddDeviceRequest BuildRequest(AddDeviceRequest shared, BulkDeviceRow row)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(row);

        var v = row.Values;
        string? Get(string key) => v.TryGetValue(key, out var value) ? value : null;
        bool? GetBool(string key) => v.TryGetValue(key, out var value) ? ParseBool(value) : null;
        int? GetInt(string key) => v.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        var pingOnly = GetBool(SnmpDisable) ?? shared.SnmpDisabled == true;

        var forceAdd = GetBool(ForceAdd) is { } force ? (force ? true : null) : shared.ForceAdd;
        var pollerGroup = GetInt(PollerGroup) is { } group ? (group == 0 ? null : group) : shared.PollerGroup;

        if (pingOnly)
        {
            return new AddDeviceRequest
            {
                Hostname = row.Hostname,
                SnmpDisabled = true,
                Os = Get(Os) ?? shared.Os,
                SysName = Get(SysName) ?? shared.SysName,
                Hardware = Get(Hardware) ?? shared.Hardware,
                PollerGroup = pollerGroup,
                ForceAdd = forceAdd,
                Location = Get(Location) ?? shared.Location,
                DisplayTemplate = Get(DisplayTemplate) ?? shared.DisplayTemplate,
                OverwriteIp = Get(OverwriteIp) ?? shared.OverwriteIp,
            };
        }

        // The shared settings may be ping-only while this row is SNMP -
        // then there's no shared SNMP version, and v2c is the default.
        var sharedVersion = shared.SnmpDisabled == true ? null : shared.SnmpVersion;
        var version = (Get(SnmpVersion) ?? sharedVersion ?? "v2c").ToLowerInvariant();
        var sameFamily = sharedVersion is not null && IsV3(version) == IsV3(sharedVersion);

        var pingFallback = GetBool(PingFallback) is { } fallback ? (fallback ? true : null) : shared.PingFallback;
        var port = GetInt(Port) ?? (shared.SnmpDisabled == true ? null : shared.Port);
        var transport = Get(Transport)?.ToLowerInvariant() ?? (shared.SnmpDisabled == true ? null : shared.Transport);

        if (IsV3(version))
        {
            return new AddDeviceRequest
            {
                Hostname = row.Hostname,
                SnmpVersion = "v3",
                AuthLevel = Get(AuthLevel) ?? (sameFamily ? shared.AuthLevel : null) ?? "authPriv",
                AuthName = Get(AuthName) ?? (sameFamily ? shared.AuthName : null),
                AuthPass = Get(AuthPass) ?? (sameFamily ? shared.AuthPass : null),
                AuthAlgo = Get(AuthAlgo) ?? (sameFamily ? shared.AuthAlgo : null) ?? "SHA",
                CryptoPass = Get(CryptoPass) ?? (sameFamily ? shared.CryptoPass : null),
                CryptoAlgo = Get(CryptoAlgo) ?? (sameFamily ? shared.CryptoAlgo : null) ?? "AES",
                Port = port,
                Transport = transport,
                PollerGroup = pollerGroup,
                ForceAdd = forceAdd,
                PingFallback = pingFallback,
                Location = Get(Location) ?? shared.Location,
                DisplayTemplate = Get(DisplayTemplate) ?? shared.DisplayTemplate,
                OverwriteIp = Get(OverwriteIp) ?? shared.OverwriteIp,
            };
        }

        return new AddDeviceRequest
        {
            Hostname = row.Hostname,
            SnmpVersion = version,
            Community = Get(Community) ?? (sameFamily ? shared.Community : null),
            Port = port,
            Transport = transport,
            PollerGroup = pollerGroup,
            ForceAdd = forceAdd,
            PingFallback = pingFallback,
            Location = Get(Location) ?? shared.Location,
            DisplayTemplate = Get(DisplayTemplate) ?? shared.DisplayTemplate,
            OverwriteIp = Get(OverwriteIp) ?? shared.OverwriteIp,
        };
    }

    /// <summary>
    /// Why LibreNMS would turn a request down before trying the device at
    /// all, or null: force add needs SNMP details to add with, since it
    /// skips the checks that would otherwise find working ones
    /// (<c>'SNMP information is required when force adding a device'</c>).
    /// </summary>
    public static string? RequestError(AddDeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ForceAdd == true && request.SnmpDisabled != true)
        {
            var hasSnmpInfo = IsV3(request.SnmpVersion)
                ? !string.IsNullOrEmpty(request.AuthLevel)
                : !string.IsNullOrEmpty(request.Community);

            if (!hasSnmpInfo)
            {
                return "Force add needs SNMP details to add with - enter a community (or v3 credentials), or turn force add off.";
            }
        }

        return null;
    }

    /// <summary>A CSV to start from: every column, with a v2c, a v3 and a ping-only example.</summary>
    public static string Template()
    {
        var rows = new[]
        {
            Columns,
            Row((Hostname, "switch1.example.com"), (SnmpVersion, "v2c"), (Community, "public"), (Location, "London")),
            Row((Hostname, "10.0.0.20"), (SnmpVersion, "v3"), (AuthLevel, "authPriv"), (AuthName, "librenms"), (AuthPass, "auth-password"), (AuthAlgo, "SHA"), (CryptoPass, "privacy-password"), (CryptoAlgo, "AES"), (Location, "London")),
            Row((Hostname, "ups1.example.com"), (SnmpDisable, "true"), (Os, "ping"), (SysName, "ups1"), (Hardware, "UPS"), (Location, "Manchester")),
        };

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.Append(string.Join(",", row)).Append("\r\n");
        }

        return builder.ToString();

        static IReadOnlyList<string> Row(params (string Column, string Value)[] cells) =>
            Columns.Select(c => cells.FirstOrDefault(cell => cell.Column == c).Value ?? string.Empty).ToList();
    }

    /// <summary>true/false, yes/no, y/n, 1/0 or on/off - null for anything else.</summary>
    public static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" or "on" => true,
        "false" or "no" or "n" or "0" or "off" or "" => false,
        _ => null,
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();

    private static bool IsV3(string? version) => string.Equals(version, "v3", StringComparison.OrdinalIgnoreCase);

    private static string? NormaliseColumn(string name)
    {
        var trimmed = name.Trim();
        if (Aliases.TryGetValue(trimmed, out var alias))
        {
            return alias;
        }

        return Columns.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static string? HostnameError(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return "No hostname.";
        }

        return IsValidHostnameOrIp(hostname) ? null : "Not a valid hostname or IP address.";
    }

    /// <summary>The same limits the single Add device dialog offers, checked per row.</summary>
    private static string? ValuesError(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (column, value) in values)
        {
            var error = column switch
            {
                SnmpVersion when value.ToLowerInvariant() is not ("v1" or "v2c" or "v3") => "snmpver must be v1, v2c or v3.",
                AuthLevel when value is not ("noAuthNoPriv" or "authNoPriv" or "authPriv") => "authlevel must be noAuthNoPriv, authNoPriv or authPriv.",
                Transport when value.ToLowerInvariant() is not ("udp" or "tcp" or "udp6" or "tcp6") => "transport must be udp, tcp, udp6 or tcp6.",
                Port when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535 => "port must be a number from 1 to 65535.",
                PollerGroup when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var group) || group < 0 => "poller_group must be a poller group's number.",
                OverwriteIp when !IPAddress.TryParse(value, out _) => "overwrite_ip must be an IP address.",
                _ when BooleanColumns.Contains(column) && ParseBool(value) is null => $"{column} must be true or false.",
                _ => null,
            };

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>The same device listed twice would only fail the second time as a duplicate - say so up front instead.</summary>
    private static List<BulkDeviceRow> MarkDuplicates(List<BulkDeviceRow> rows)
    {
        var firstLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Hostname.Length == 0)
            {
                continue;
            }

            if (firstLine.TryGetValue(row.Hostname, out var line))
            {
                if (row.Error is null)
                {
                    rows[i] = row with { Error = $"Already listed on line {line}." };
                }
            }
            else
            {
                firstLine[row.Hostname] = row.Line;
            }
        }

        return rows;
    }
}
