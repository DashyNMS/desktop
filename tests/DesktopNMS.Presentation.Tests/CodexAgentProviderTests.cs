using System.Text.Json.Nodes;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Services.Intelligence.Agents;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>
/// The Codex adapter's reading of app-server output and its command line
/// (#332), from responses shaped as Codex 0.167 sends them, with synthetic values.
/// </summary>
public sealed class CodexAgentProviderTests
{
    [Fact]
    public void A_signed_in_ChatGPT_account_is_ready_with_a_digest_for_its_chats()
    {
        var read = JsonNode.Parse("""
            {"account":{"type":"chatgpt","email":"someone@example.net","planType":"plus","workspaceName":null},
             "requiresOpenaiAuth":true,"workspaceRouting":{"chatgptAccountId":"00000000-0000-4000-8000-000000000001"}}
            """);

        var status = CodexAgentProvider.Status("codex", "0.167.0", read);

        Assert.True(status.IsReady);
        Assert.Equal("Signed in as someone@example.net (ChatGPT Plus)", status.AccountText);
        Assert.Equal(32, status.AccountKey!.Length);
        Assert.DoesNotContain("example.net", status.AccountKey);
        Assert.NotEqual(CodexAgentProvider.Status("codex", "0.167.0", JsonNode.Parse("""{"account":{"type":"chatgpt","email":"other@example.net"}}""")).AccountKey, status.AccountKey);
    }

    [Fact]
    public void No_account_means_signed_out()
    {
        var status = CodexAgentProvider.Status("codex", "0.167.0", JsonNode.Parse("""{"account":null,"requiresOpenaiAuth":true}"""));

        Assert.Equal(AgentProviderState.SignedOut, status.State);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void An_API_key_says_where_usage_is_billed()
    {
        var status = CodexAgentProvider.Status("codex", "0.167.0", JsonNode.Parse("""{"account":{"type":"apiKey"}}"""));

        Assert.True(status.IsReady);
        Assert.Contains("API key", status.AccountText);
    }

    [Fact]
    public void Models_offer_their_reasoning_levels_without_delegation()
    {
        var model = JsonNode.Parse("""
            {"id":"model-a","model":"model-a","displayName":"Model A","description":"Everyday work.","hidden":false,
             "supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"high"},{"reasoningEffort":"ultra"}],
             "defaultReasoningEffort":"low","isDefault":true}
            """)!.AsObject();

        var parsed = CodexAgentProvider.Model(model, "model-a");

        Assert.Equal("Model A", parsed.Name);
        Assert.Equal(["low", "medium", "high"], parsed.Efforts);
        Assert.Equal("low", parsed.DefaultEffort);
    }

    [Fact]
    public void Rate_limits_become_measured_usage_windows()
    {
        var limits = JsonNode.Parse("""
            {"limitId":"codex","primary":{"usedPercent":98,"windowDurationMins":10080,"resetsAt":1795269917},
             "secondary":null,"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"rateLimitReachedType":null}
            """);

        var usage = CodexAgentProvider.Usage(limits);

        var window = Assert.Single(usage!.Windows);
        Assert.Equal("weekly", window.Label);
        Assert.Equal(98, window.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795269917), window.ResetsAt);
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void No_rate_limits_means_no_usage_rather_than_zero()
    {
        Assert.Null(CodexAgentProvider.Usage(null));
        Assert.Null(CodexAgentProvider.Usage(JsonNode.Parse("""{"primary":null,"secondary":null}""")));
    }

    [Theory]
    [InlineData(300L, "5-hour")]
    [InlineData(10080L, "weekly")]
    [InlineData(1440L, "daily")]
    [InlineData(90L, "90-minute")]
    public void Windows_are_named_by_their_length(long minutes, string label)
        => Assert.Equal(label, CodexAgentProvider.WindowLabel(minutes));

    [Fact]
    public void The_command_line_switches_off_Codex_tools_and_the_users_servers_and_holds_no_secret()
    {
        var known = new HashSet<string>(["shell_tool", "apps", "plugins", "multi_agent", "web_search_request", "code_mode_host"]);

        var args = CodexAgentProvider.Arguments(known, ["node_repl", "my.server"], new Uri("http://127.0.0.1:50000/mcp"));

        Assert.Equal("app-server", args[0]);
        Assert.Contains("shell_tool", args);
        Assert.Contains("plugins", args);
        Assert.DoesNotContain("computer_use", args);
        Assert.DoesNotContain("code_mode_host", args);
        Assert.Contains("web_search=\"disabled\"", args);
        Assert.Contains("sandbox_mode=\"read-only\"", args);
        Assert.Contains("mcp_servers.node_repl.enabled=false", args);
        Assert.Contains("mcp_servers={\"my.server\"={enabled=false}}", args);
        Assert.Contains($"mcp_servers.{CodexAgentProvider.BridgeServerName}.url=\"http://127.0.0.1:50000/mcp\"", args);
        Assert.Contains($"mcp_servers.{CodexAgentProvider.BridgeServerName}.bearer_token_env_var=\"{CodexAgentProvider.TokenVariable}\"", args);
    }

    [Fact]
    public void Live_web_search_only_when_allowed()
    {
        Assert.Contains("web_search=\"live\"", CodexAgentProvider.Arguments(new HashSet<string>(), [], null, webSearch: true));
        Assert.Contains("web_search=\"disabled\"", CodexAgentProvider.Arguments(new HashSet<string>(), [], null, webSearch: false));
    }

    [Fact]
    public void A_status_only_server_has_no_bridge()
    {
        var args = CodexAgentProvider.Arguments(new HashSet<string>(), [], bridge: null);

        Assert.DoesNotContain(args, a => a.Contains(CodexAgentProvider.BridgeServerName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"usageLimitExceeded\"", IntelligenceFailure.PlanUsage)]
    [InlineData("\"unauthorized\"", IntelligenceFailure.PlanUsage)]
    [InlineData("{\"responseStreamDisconnected\":{\"httpStatusCode\":null}}", IntelligenceFailure.Network)]
    [InlineData("\"other\"", IntelligenceFailure.Model)]
    public void Turn_errors_are_worded_for_people(string info, IntelligenceFailure failure)
    {
        var error = JsonNode.Parse("{\"message\":\"raw detail\",\"codexErrorInfo\":" + info + "}");

        var ex = CodexAgentProvider.Server.Failure(error, null);

        Assert.Equal(failure, ex.Failure);
        Assert.DoesNotContain("raw detail", ex.Message);
    }

    [Fact]
    public void A_signed_out_error_says_how_to_sign_in()
    {
        var ex = CodexAgentProvider.Server.Failure(JsonNode.Parse("""{"message":"x","codexErrorInfo":"unauthorized"}"""), null);

        Assert.Equal(AgentProviderState.SignedOut, ex.State);
        Assert.Contains("codex login", ex.Message);
    }

    [Fact]
    public void The_users_own_MCP_servers_are_read_from_the_config_file()
    {
        var toml = """
            model = "model-a"

            [mcp_servers.DashyNMS]
            command = "docker"

            [mcp_servers.DashyNMS.env]
            LIBRENMS_URL = "https://librenms.example.net"

            [mcp_servers."my.server"]
            url = "https://mcp.example.net"

              [ mcp_servers.node_repl ]
            command = "node"
            """;

        Assert.Equal(["DashyNMS", "my.server", "node_repl"], CodexAgentProvider.ServersIn(toml));
    }

    [Fact]
    public void A_missing_config_file_means_no_servers_to_switch_off()
        => Assert.Empty(CodexAgentProvider.ConfiguredMcpServers(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

    [Theory]
    [InlineData("thread not found: example", true)]
    [InlineData("model not found: example-model", false)]
    [InlineData("no rollout found for thread id", true)]
    [InlineData("model is not available on this account", false)]
    [InlineData("rate limit reached", false)]
    public void Only_a_missing_thread_counts_as_a_lost_conversation(string message, bool lost)
        => Assert.Equal(lost, CodexAgentProvider.IsMissingThread(message));
}
