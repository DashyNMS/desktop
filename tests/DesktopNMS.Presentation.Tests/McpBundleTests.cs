using DesktopNMS.Demo;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class McpBundleTests
{
    [McpBundleFact]
    public async Task The_native_bundle_initializes_lists_tools_and_queries_the_demo()
    {
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("DASHYNMS_TEST_MCP")!);
        Assert.True(File.Exists(executable), "The native network-tools executable is missing.");
        var metadata = await DesktopNMS.Services.ComponentVersions.ReadMcpAsync(executable);
        Assert.Equal("Bundled", metadata.State);
        Assert.NotEqual(DesktopNMS.Services.ComponentVersions.Unavailable, metadata.Version);
        using var server = new DemoServer(new DemoFleet(), NullLogger.Instance);
        server.Start();
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["LIBRENMS_URL"] = server.Address.ToString();
        environment["LIBRENMS_TOKEN"] = "example-token";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Packaged network tools",
            Command = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        }, NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "find_device");
        var response = await client.CallToolAsync("find_device", new Dictionary<string, object?> { ["query"] = "core" }, cancellationToken: timeout.Token);
        Assert.False(response.IsError == true);
        var text = string.Join("\n", response.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("core-sw", text, StringComparison.OrdinalIgnoreCase);
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
