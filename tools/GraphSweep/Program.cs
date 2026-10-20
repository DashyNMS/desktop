// Graph sweep (#301): which graphs and sensor readings the app fails on.
//
//   dotnet run --project tools/GraphSweep -- [--devices 50|all] [--out <file>]
//
// Uses the server and API token DashyNMS is signed in with (settings.json and
// token.dat for the current Windows user). Read-only GETs, one at a time with
// a short pause, so LibreNMS isn't loaded. For a random sample of devices it
// fetches every graph the app offers (device, health, wireless, and each
// sensor class's own graph) and runs it through Core's GraphSvgStyle and
// GraphLegend as the Graphs section does, recording anything that throws or
// wouldn't line up. It also checks every sensor reading in resources/sensors.
//
// The report (TSV, default in %TEMP%) holds device and sensor ids, graph
// names, sensor classes, raw reading values and error types - never
// hostnames, descriptions or messages - so it can be shared.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Graphs;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

var sample = 50;
var output = Path.Combine(Path.GetTempPath(), $"dashynms-graph-sweep-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--devices" && i + 1 < args.Length)
    {
        sample = args[++i] == "all" ? int.MaxValue : int.Parse(args[i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--out" && i + 1 < args.Length)
    {
        output = Path.GetFullPath(args[++i]);
    }
}

var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DashyNMS");
using var settingsJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "settings.json")));
var serverUrl = settingsJson.RootElement.GetProperty("serverUrl").GetString();
if (!LibreNmsConnection.TryParseWebRoot(serverUrl, out var webRoot, out var error) || webRoot is null)
{
    Console.Error.WriteLine("DashyNMS isn't signed in to a server: " + error);
    return 1;
}

var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(
    File.ReadAllBytes(Path.Combine(data, "token.dat")),
    Encoding.UTF8.GetBytes("DesktopNMS.LibreNMS.ApiToken.v1"),
    DataProtectionScope.CurrentUser));

var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
transport.Configure(new LibreNmsConnection(webRoot, token, timeoutSeconds: 60));
using var client = new LibreNmsClient(transport, NullLogger<LibreNmsClient>.Instance);

var rows = new List<string> { "device_id\tkind\titem\toutcome\tdetail" };
var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
var clock = Stopwatch.StartNew();
var requests = 0;

void Record(int? deviceId, string kind, string item, string outcome, string detail = "")
{
    rows.Add(string.Join('\t', deviceId?.ToString(CultureInfo.InvariantCulture) ?? "", kind, item, outcome, Clean(detail)));
    counts[kind + " " + outcome] = counts.GetValueOrDefault(kind + " " + outcome) + 1;
}

async Task<T> Get<T>(Func<Task<T>> request)
{
    await Task.Delay(150).ConfigureAwait(false);
    requests++;
    return await request().ConfigureAwait(false);
}

// ---------------------------------------------------------------- sensor readings
Console.WriteLine("Checking sensor readings...");
var rawSensors = "{}";
try
{
    rawSensors = await Get(() => transport.SendRawAsync("resources/sensors"));
}
catch (LibreNmsApiException ex)
{
    // A token without LibreNMS's "user" role gets 403 here (#294); the graphs are still worth sweeping.
    Record(null, "sensor-list", "resources/sensors", "failed", Describe(ex));
}

using (var doc = JsonDocument.Parse(rawSensors))
{
    if (doc.RootElement.TryGetProperty("sensors", out var list) && list.ValueKind == JsonValueKind.Array)
    {
        foreach (var sensor in list.EnumerateArray())
        {
            var id = Int(sensor, "sensor_id");
            var device = Int(sensor, "device_id");
            var sensorClass = Text(sensor, "sensor_class") ?? "?";
            foreach (var field in new[] { "sensor_current", "sensor_limit", "sensor_limit_warn", "sensor_limit_low", "sensor_limit_low_warn" })
            {
                if (!sensor.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Number or JsonValueKind.Null)
                {
                    continue;
                }

                var literal = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
                var parses = double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
                if (!parses)
                {
                    Record(device, "sensor-value", $"{sensorClass}/{id}/{field}", "not-a-number", literal);
                }
            }
        }

        Record(null, "sensor-list", "resources/sensors", "listed", list.GetArrayLength().ToString(CultureInfo.InvariantCulture));
    }
}

IReadOnlyList<Sensor> allSensors = Array.Empty<Sensor>();
try
{
    allSensors = await Get(() => client.Sensors.ListAsync());
    Record(null, "sensor-list", "typed", "ok", allSensors.Count.ToString(CultureInfo.InvariantCulture));
}
catch (Exception ex)
{
    Record(null, "sensor-list", "typed", "failed", Describe(ex));
}

foreach (var unknown in allSensors.Select(s => s.SensorClass ?? "").Distinct().Where(c => GraphLegend.FormatSensor(0, c) == "0").Order())
{
    Record(null, "sensor-class", unknown, "no-unit", allSensors.Count(s => s.SensorClass == unknown).ToString(CultureInfo.InvariantCulture));
}

// ---------------------------------------------------------------- graphs
var devices = (await Get(() => client.Devices.ListAsync())).Where(d => !d.Disabled).ToList();
var chosen = devices.OrderBy(_ => Random.Shared.Next()).Take(sample).ToList();
Console.WriteLine($"Sweeping {chosen.Count} of {devices.Count} devices...");

foreach (var (device, index) in chosen.Select((d, i) => (d, i)))
{
    Console.Write($"\r  {index + 1}/{chosen.Count}  ({requests} requests, {clock.Elapsed:mm\\:ss})   ");
    var id = device.DeviceId;
    var graphs = new List<GraphType>();
    foreach (var (listing, list) in new (string, Func<Task<IReadOnlyList<GraphType>>>)[]
             {
                 ("graphs", () => client.Graphs.ListAsync(id)),
                 ("health", () => client.Graphs.ListHealthAsync(id)),
                 ("wireless", () => client.Graphs.ListWirelessAsync(id)),
             })
    {
        try
        {
            graphs.AddRange(await Get(list));
        }
        catch (Exception ex)
        {
            Record(id, "listing", listing, "failed", Describe(ex));
        }
    }

    var deviceSensors = allSensors.Where(s => s.DeviceId == id).ToList();
    foreach (var graph in graphs.DistinctBy(g => g.Name))
    {
        await CheckGraph(id, graph.Name, deviceSensors, sensorId: null);
    }

    // Each sensor class's own single-sensor graph, as the Graphs section fetches when one series is left.
    foreach (var first in deviceSensors.GroupBy(s => s.SensorClass).Select(g => g.First()))
    {
        await CheckGraph(id, "device_" + first.SensorClass, deviceSensors, first.SensorId);
    }
}

Console.WriteLine();
await File.WriteAllLinesAsync(output, rows);
Console.WriteLine($"{requests} requests in {clock.Elapsed:mm\\:ss}. Report: {output}");
foreach (var (key, count) in counts)
{
    Console.WriteLine($"  {count,6}  {key}");
}

return 0;

async Task CheckGraph(int deviceId, string graphName, IReadOnlyList<Sensor> deviceSensors, int? sensorId)
{
    var kind = sensorId is null ? "graph" : "sensor-graph";
    var item = sensorId is null ? graphName : $"{graphName}/{sensorId}";
    string svg;
    try
    {
        svg = sensorId is { } sid
            ? await Get(() => client.Graphs.GetSensorSvgAsync(deviceId, graphName, sid, GraphTimeRange.LastDay, 1000, 520, legend: false))
            : await Get(() => client.Graphs.GetSvgAsync(deviceId, graphName, GraphTimeRange.LastDay, 1000, 520, legend: true));
    }
    catch (Exception ex)
    {
        Record(deviceId, kind, item, "fetch-failed", Describe(ex));
        return;
    }

    if (!svg.TrimStart().StartsWith('<') || !svg.Contains("<svg", StringComparison.OrdinalIgnoreCase))
    {
        Record(deviceId, kind, item, "not-svg", $"{svg.Length} chars");
        return;
    }

    var notes = new List<string>();
    if (NanText().IsMatch(svg))
    {
        notes.Add("svg-mentions-nan");
    }

    try
    {
        var plain = GraphSvgStyle.Restyle(svg, GraphPalette.Dark);
        if (sensorId is not null)
        {
            Record(deviceId, kind, item, notes.Count == 0 ? "ok" : string.Join(',', notes));
            return;
        }

        var cropped = GraphSvgStyle.Restyle(svg, GraphPalette.Dark, new HashSet<int>(), cropLegend: true);
        IReadOnlyList<GraphLegendEntry>? entries = null;
        if (GraphLegend.SensorClassOf(graphName) is { } sensorClass)
        {
            entries = GraphLegend.ForSensors(deviceSensors, sensorClass);
            if (entries.Count == 0 && deviceSensors.Count > 0)
            {
                notes.Add("no-sensors-of-class");
            }
        }

        // The Graphs section names the series when the counts agree, then
        // colours each from the cropped graph's series - fewer series than
        // names is an IndexOutOfRange there.
        if (entries is { Count: > 0 } && entries.Count == plain.LegendColours.Count && cropped.SeriesColours.Count < entries.Count)
        {
            notes.Add($"fewer-series-than-legend({cropped.SeriesColours.Count}<{entries.Count})");
        }
        else if (entries is { Count: > 0 } && entries.Count != plain.LegendColours.Count)
        {
            notes.Add($"legend-count-differs({entries.Count}!={plain.LegendColours.Count})");
        }

        if (entries?.Any(e => e.Value is { } v && (v.Contains("NaN", StringComparison.Ordinal) || v.Contains("Infinity", StringComparison.Ordinal) || v.Contains('∞'))) == true)
        {
            notes.Add("legend-value-not-a-number");
        }

        Record(deviceId, kind, item, notes.Count == 0 ? "ok" : string.Join(',', notes));
    }
    catch (Exception ex)
    {
        Record(deviceId, kind, item, "restyle-threw", Describe(ex));
    }
}

static string Describe(Exception ex) => ex switch
{
    LibreNmsApiException api => $"{api.GetType().Name} {(int?)api.StatusCode} {api.ServerMessage}",
    _ => ex.GetType().Name + (ex.InnerException is { } inner ? " <- " + inner.GetType().Name : ""),
};

static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') is var t && t.Length > 120 ? t[..120] : text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

static int? Int(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number
       : element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;

static string? Text(JsonElement element, string name)
    => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

partial class Program
{
    [GeneratedRegex(@"(?<![A-Za-z])-?nan(?![A-Za-z])|(?<![A-Za-z])-?inf(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex NanText();
}
