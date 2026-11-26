using System.Text.Json.Nodes;
using DesktopNMS.Services.Intelligence;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>What the model reads from an MCP result: the server's own words, whole, with images kept apart.</summary>
public sealed class McpToolResultTests
{
    private const string Uncertain = """{"status":"ok","summary":"Sensors could not be listed for 2 devices.","data":{"readings":[],"unavailable":[{"source":"sensors","reason":"denied"}],"omitted":{"devices":2}}}""";

    [Fact]
    public void Text_blocks_reach_the_model_unchanged_and_in_order()
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = Uncertain }, new TextContentBlock { Text = "second" }] };

        Assert.Equal(Uncertain + "\nsecond", StdioMcpToolProcess.Text(result));
    }

    [Fact]
    public void Image_bytes_never_enter_the_text_and_the_notice_beside_them_does()
    {
        const string Partial = """{"status":"ok","summary":"Some graph listings couldn't be read, so the available types are incomplete."}""";
        var result = new CallToolResult { Content = [new TextContentBlock { Text = Partial }, ImageContentBlock.FromBytes(new byte[] { 137, 80, 78, 71 }, "image/png")] };

        Assert.Equal(Partial, StdioMcpToolProcess.Text(result));
    }

    [Fact]
    public void Structured_content_is_read_only_when_the_server_sent_no_text()
    {
        var structured = System.Text.Json.JsonDocument.Parse(Uncertain).RootElement;
        var withText = new CallToolResult { Content = [new TextContentBlock { Text = "text wins" }], StructuredContent = structured };
        var withoutText = new CallToolResult { Content = [], StructuredContent = structured };

        Assert.Equal("text wins", StdioMcpToolProcess.Text(withText));
        Assert.Equal(JsonNode.Parse(Uncertain)!.ToJsonString(), JsonNode.Parse(StdioMcpToolProcess.Text(withoutText))!.ToJsonString());
    }

    [Fact]
    public void An_empty_result_is_an_empty_object_never_a_success_with_data()
    {
        Assert.Equal("{}", StdioMcpToolProcess.Text(new CallToolResult { Content = [] }));
    }
}
