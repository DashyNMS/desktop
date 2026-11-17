using System.Text.Json;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Demo;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class McpBundleTests
{
    // Tools whose source isn't configured against the demo: they must say so, not fail.
    private static readonly HashSet<string> NeedsUnimusOrGraylog = new(StringComparer.Ordinal)
    {
        "config_backup", "config_diff", "recent_config_changes", "search_logs",
    };

    [McpBundleFact]
    public async Task The_native_bundle_initializes_lists_tools_and_queries_the_demo()
    {
        var metadata = await DesktopNMS.Services.ComponentVersions.ReadMcpAsync(Executable);
        Assert.Equal("Bundled", metadata.State);
        Assert.NotEqual(DesktopNMS.Services.ComponentVersions.Unavailable, metadata.Version);
        await using var demo = await BundledServer.StartAsync();
        var tools = await demo.Client.ListToolsAsync(cancellationToken: demo.Token);
        Assert.Contains(tools, tool => tool.Name == "find_device");
        var text = await demo.CallAsync("find_device", new() { ["query"] = "core" });
        Assert.Contains("core-sw", text, StringComparison.OrdinalIgnoreCase);
    }

    [McpBundleFact]
    public async Task Every_allowlisted_tool_is_offered_with_a_description_and_an_object_schema()
    {
        await using var demo = await BundledServer.StartAsync();
        var tools = (await demo.Client.ListToolsAsync(cancellationToken: demo.Token)).ToDictionary(t => t.Name, StringComparer.Ordinal);

        Assert.Equal(24, IntelligenceToolAllowlist.Names.Count);
        foreach (var name in IntelligenceToolAllowlist.Names)
        {
            Assert.True(tools.TryGetValue(name, out var tool), $"{name} isn't offered by the bundled server.");
            Assert.False(string.IsNullOrWhiteSpace(tool!.Description), $"{name} has no description.");
            Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
            if (tool.JsonSchema.TryGetProperty("required", out var required))
            {
                var properties = tool.JsonSchema.GetProperty("properties");
                foreach (var property in required.EnumerateArray())
                    Assert.True(properties.TryGetProperty(property.GetString()!, out _), $"{name} requires an undeclared {property}.");
            }
        }
    }

    [McpBundleFact]
    public async Task Every_allowlisted_tool_answers_an_ordinary_question_with_a_bounded_json_result()
    {
        await using var demo = await BundledServer.StartAsync();
        var alerts = JsonDocument.Parse(await demo.CallAsync("active_alerts", new()));
        var alertId = alerts.RootElement.GetProperty("data").GetProperty("devices").EnumerateArray()
            .SelectMany(d => d.GetProperty("alerts").EnumerateArray()).First().GetProperty("alert_id").GetInt32();
        var calls = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["active_alerts"] = new(),
            ["alert_details"] = new() { ["alert_id"] = alertId },
            ["alert_rules"] = new(),
            ["config_backup"] = new() { ["device"] = "core-sw-01" },
            ["config_diff"] = new() { ["device"] = "core-sw-01" },
            ["device_details"] = new() { ["device"] = "core-sw-01" },
            ["device_events"] = new() { ["device"] = "core-sw-02" },
            ["device_health"] = new() { ["device"] = "core-sw-01" },
            ["device_neighbours"] = new() { ["device"] = "core-sw-01" },
            ["device_sensors"] = new() { ["device"] = "core-sw-02" },
            ["find_device"] = new() { ["query"] = "core" },
            ["graph"] = new() { ["device"] = "core-sw-01" },
            ["health_overview"] = new(),
            ["list_groups_and_locations"] = new(),
            ["network_events"] = new(),
            ["network_overview"] = new(),
            ["port_status"] = new() { ["device"] = "core-sw-01", ["port"] = "Te1/1/1" },
            ["recent_config_changes"] = new(),
            ["search_logs"] = new() { ["text"] = "error" },
            ["top_devices"] = new(),
            ["top_errors"] = new(),
            ["top_interfaces"] = new(),
            ["trace_endpoint"] = new() { ["address"] = "192.0.2.10" },
            ["what_changed"] = new() { ["device"] = "core-sw-02" },
        };
        Assert.Equal(IntelligenceToolAllowlist.Names.OrderBy(n => n), calls.Keys.OrderBy(n => n));

        foreach (var (name, arguments) in calls)
        {
            var response = await demo.Client.CallToolAsync(name, arguments, cancellationToken: demo.Token);
            var text = BundledServer.Text(response);
            if (NeedsUnimusOrGraylog.Contains(name))
            {
                Assert.True(text.Contains("Unimus", StringComparison.Ordinal) || text.Contains("Graylog", StringComparison.Ordinal), $"{name} didn't say its source isn't set up: {text}");
                continue;
            }

            if (name == "graph")
            {
                // The demo draws SVG, which the tool refuses; the refusal must stay a fixed message.
                Assert.DoesNotContain("example-token", text, StringComparison.Ordinal);
                Assert.DoesNotContain(demo.Address, text, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            Assert.False(response.IsError == true, $"{name} failed: {text}");
            using var json = JsonDocument.Parse(text);
            Assert.True(json.RootElement.GetProperty("status").GetString() == "ok", $"{name} reported: {text}");
            // MCP_MAX_RESULT_BYTES' default; graph images travel as separate blocks.
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 16_384, $"{name} returned {text.Length} characters.");
        }
    }

    [McpBundleFact]
    public void The_bundle_carries_the_network_tools_licence_and_every_dependency_notice()
    {
        // A Mac app keeps them in Resources/mcp, out of the signed code directory.
        var folder = Path.GetDirectoryName(Executable)!;
        var macResources = Path.GetFullPath(Path.Combine(folder, "..", "..", "Resources", "mcp"));
        if (folder.EndsWith(Path.Combine("Contents", "MacOS", "mcp"), StringComparison.Ordinal)) folder = macResources;

        foreach (var file in new[] { "LICENSE", "NOTICES.md", "BUILD.txt", "version.json", Path.Combine("third-party", "inventory.json") })
            Assert.True(File.Exists(Path.Combine(folder, file)), $"The network tools are missing {file}.");

        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "third-party", "inventory.json")));
        var packages = inventory.RootElement.EnumerateArray().ToList();
        Assert.Contains(packages, p => p.GetProperty("package").GetString()!.StartsWith("DashyNMS.Core/", StringComparison.Ordinal));
        Assert.Contains(packages, p => p.GetProperty("package").GetString()!.StartsWith("ModelContextProtocol.Core/", StringComparison.Ordinal));
        foreach (var package in packages)
        {
            var identity = package.GetProperty("package").GetString()!.Split('/');
            var packageFolder = Path.Combine(folder, "third-party", identity[0].ToLowerInvariant(), identity[1]);
            foreach (var file in package.GetProperty("files").EnumerateObject())
                Assert.True(File.Exists(Path.Combine(packageFolder, file.Name)), $"{identity[0]} is missing {file.Name}.");
        }

        var runtime = Assert.Single(Directory.GetDirectories(Path.Combine(folder, "third-party"), "microsoft.netcore.app.runtime.*"));
        Assert.True(File.Exists(Path.Combine(runtime, "LICENSE.TXT")));
        Assert.True(File.Exists(Path.Combine(runtime, "THIRD-PARTY-NOTICES.TXT")));
    }

    [McpBundleFact]
    public async Task The_app_s_own_start_path_runs_the_bundle_with_its_saved_settings()
    {
        using var server = new DemoServer(new DemoFleet(), NullLogger.Instance);
        server.Start();
        var settings = new DesktopNMS.Core.Configuration.AppSettings { OverrideSensorLimitsWithAppThresholds = true };
        settings.TemperatureThresholds.HighWarning = 20;
        settings.TemperatureThresholds.HighCritical = 25;
        var values = McpToolHost.ToolSettings(new DesktopNMS.Core.Api.LibreNmsConnection(server.Address, "example-token"), settings, () => null, () => null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using var process = await StdioMcpToolProcess.StartAsync(Executable, values, NullLogger.Instance, timeout.Token);
        var result = await process.CallAsync("device_sensors", new Dictionary<string, object?> { ["device"] = "core-sw-01", ["type"] = "temperature" }, timeout.Token);

        Assert.Equal(IntelligenceToolAllowlist.Names.Count, process.Tools.Count);
        Assert.False(result.IsError, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.All(json.RootElement.GetProperty("data").GetProperty("readings").EnumerateArray(),
            reading => Assert.Equal("critical", reading.GetProperty("state").GetString()));
    }

    [McpBundleFact]
    public async Task Sources_that_aren_t_set_up_reach_the_model_as_unknown_not_as_nothing_changed()
    {
        await using var demo = await BundledServer.StartAsync();
        await using var process = await StdioMcpToolProcess.StartAsync(Executable,
            McpToolHost.ToolSettings(new DesktopNMS.Core.Api.LibreNmsConnection(new Uri(demo.Address), "example-token"), new(), () => null, () => null),
            NullLogger.Instance, demo.Token);

        var result = await process.CallAsync("what_changed", new Dictionary<string, object?> { ["device"] = "core-sw-02" }, demo.Token);

        using var json = JsonDocument.Parse(result.Output);
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("unimus").GetProperty("configured").GetBoolean());
        Assert.False(data.GetProperty("graylog").GetProperty("configured").GetBoolean());
        Assert.Contains("Unimus isn't configured", json.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
    }

    [McpBundleFact]
    public async Task A_refused_start_gives_a_fixed_reason_and_none_of_the_server_s_output()
    {
        using var server = new DemoServer(new DemoFleet(), NullLogger.Instance);
        server.Start();
        var values = McpToolHost.ToolSettings(new DesktopNMS.Core.Api.LibreNmsConnection(server.Address, "secret-example-token"), new(), () => null, () => null)
            .Where(p => p.Key != "MCP_TEMPERATURE_HIGH_WARNING")
            .Append(new("MCP_TEMPERATURE_HIGH_WARNING", "900"))
            .ToList();

        var failure = await Assert.ThrowsAsync<IntelligenceException>(() => StdioMcpToolProcess.StartAsync(Executable, values, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(IntelligenceFailure.Tools, failure.Failure);
        Assert.DoesNotContain("secret-example-token", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP_", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("900", failure.Message, StringComparison.Ordinal);
    }

    internal static string Executable => Path.GetFullPath(Environment.GetEnvironmentVariable("DASHYNMS_TEST_MCP")!);

    /// <summary>The bundled server over stdio against the demo LibreNMS, with only the environment given.</summary>
    internal sealed class BundledServer : IAsyncDisposable
    {
        private readonly DemoServer _server;
        private readonly CancellationTokenSource _timeout;

        private BundledServer(DemoServer server, CancellationTokenSource timeout, McpClient client)
        {
            _server = server;
            _timeout = timeout;
            Client = client;
        }

        public McpClient Client { get; }
        public CancellationToken Token => _timeout.Token;
        public string Address => _server.Address.ToString();

        public static async Task<BundledServer> StartAsync(IReadOnlyDictionary<string, string>? settings = null)
        {
            Assert.True(File.Exists(Executable), "The native network-tools executable is missing.");
            var server = new DemoServer(new DemoFleet(), NullLogger.Instance);
            server.Start();
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["LIBRENMS_URL"] = server.Address.ToString();
            environment["LIBRENMS_TOKEN"] = "example-token";
            foreach (var (key, value) in settings ?? new Dictionary<string, string>()) environment[key] = value;
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Packaged network tools",
                Command = Executable,
                WorkingDirectory = Path.GetDirectoryName(Executable),
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            }, NullLoggerFactory.Instance);
            try
            {
                return new(server, timeout, await McpClient.CreateAsync(transport, cancellationToken: timeout.Token));
            }
            catch
            {
                server.Dispose();
                timeout.Dispose();
                throw;
            }
        }

        public async Task<string> CallAsync(string name, Dictionary<string, object?> arguments)
        {
            var response = await Client.CallToolAsync(name, arguments, cancellationToken: Token);
            var text = Text(response);
            Assert.False(response.IsError == true, $"{name} failed: {text}");
            return text;
        }

        public static string Text(CallToolResult response)
            => string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text));

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _server.Dispose();
            _timeout.Dispose();
        }
    }
}

public sealed class McpBundleFactAttribute : FactAttribute
{
    public McpBundleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DASHYNMS_TEST_MCP")))
            Skip = "Set DASHYNMS_TEST_MCP to the native executable to run the package integration check.";
    }
}
