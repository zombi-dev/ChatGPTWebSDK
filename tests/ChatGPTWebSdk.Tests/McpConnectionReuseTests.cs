using System.Text.Json.Nodes;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpConnectionReuseTests
{
    private static async Task<JsonObject> Stats(Uri endpoint)
    {
        using var http = new HttpClient();
        return JsonNode.Parse(await http.GetStringAsync(new Uri(endpoint, "/_stats")))!.AsObject();
    }
    private static McpConversationOptions Options(Uri endpoint, bool reuse = true) => new()
    {
        Servers = [new() { Label = "calculator", Endpoint = endpoint, Transport = McpWebTransport.StreamableHttp }],
        ReuseConnections = reuse, DiscoveryRetryDelay = TimeSpan.Zero, ToolTimeout = TimeSpan.FromSeconds(5)
    };
    private static void FinalOnly(FakeWebHandler handler) => handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, "[[final:" + McpBridgeTests.Nonce(body) + "]]Ready");

    [Theory, InlineData(true), InlineData(false)]
    public async Task Consecutive_messages_refresh_discovery_with_one_or_two_owned_sessions(bool reuse)
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync("http-json");
        using var handler = new FakeWebHandler(); FinalOnly(handler);
        await using (var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), Options(fixture.Endpoint, reuse)))
        {
            await client.SendAsync(new("account", "alice"), "First", "gpt-6"); await client.SendAsync(new("account", "alice"), "Next", "gpt-6");
            var stats = await Stats(fixture.Endpoint); Assert.Equal(reuse ? 1 : 2, stats["initializations"]!.GetValue<int>()); Assert.Equal(4, stats["lists"]!.GetValue<int>());
        }
        Assert.Equal(reuse ? 1 : 2, (await Stats(fixture.Endpoint))["deletes"]!.GetValue<int>());
    }

    [Theory, InlineData("http-flaky-list", 1), InlineData("http-expiring", 2)]
    public async Task Discovery_recovers_a_transient_or_expired_session_before_any_tool_call(string mode, int turns)
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync(mode);
        using var handler = new FakeWebHandler(); FinalOnly(handler);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), Options(fixture.Endpoint));
        for (var i = 0; i < turns; i++) Assert.Equal("Ready", (await client.SendAsync(new("account", "alice"), "Next", "gpt-6")).Text);
        var stats = await Stats(fixture.Endpoint); Assert.Equal(2, stats["initializations"]!.GetValue<int>()); Assert.Equal(0, stats["calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task User_and_thread_scopes_do_not_share_remote_server_sessions()
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync("http-json");
        using var handler = new FakeWebHandler(); FinalOnly(handler);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), Options(fixture.Endpoint));
        foreach (var scope in new[] { new ConversationScope("account", "alice", "one"), new("account", "bob", "one"), new("account", "alice", "two") })
            await client.SendAsync(scope, "Next", "gpt-6");
        Assert.Equal(3, (await Stats(fixture.Endpoint))["initializations"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_SSE_cursor_resumes_the_pending_result_with_GET_without_repeating_the_tool_POST()
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync("http-resume");
        await using var client = await McpWebClient.ConnectAsync(new() { Label = "calculator", Endpoint = fixture.Endpoint, Transport = McpWebTransport.StreamableHttp });
        var result = await client.CallToolAsync("add", new() { ["a"] = 2, ["b"] = 3 });
        Assert.Equal(5, result["structuredContent"]!["sum"]!.GetValue<int>());
        var stats = await Stats(fixture.Endpoint); Assert.Equal(1, stats["calls"]!.GetValue<int>()); Assert.Equal(1, stats["resumptions"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_lost_tool_result_stays_uncertain_and_is_not_replayed_on_the_next_message()
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync("http-uncertain");
        using var handler = new FakeWebHandler();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)));
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), Options(fixture.Endpoint));
        var scope = new ConversationScope("account", "alice");
        for (var i = 0; i < 2; i++) Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(scope, "Add", "gpt-6"))).Code);
        Assert.Single(await client.GetPendingMcpToolCallsAsync(scope)); Assert.Equal(1, (await Stats(fixture.Endpoint))["calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_reused_stdio_process_keeps_its_pid_and_stops_at_client_disposal()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-server.mjs");
        await using var pool = new McpConnectionPool(new());
        var config = new McpServerConfiguration { Label = "local", Command = "node", Arguments = [fixture] };
        var scope = new ConversationScope("account", "alice"); int pid;
        await using (var first = await pool.AcquireAsync(scope, config, default))
            pid = (await first.CallToolAsync("get_pid", new()))["structuredContent"]!["pid"]!.GetValue<int>();
        await using (var next = await pool.AcquireAsync(scope, config, default))
            Assert.Equal(pid, (await next.CallToolAsync("get_pid", new()))["structuredContent"]!["pid"]!.GetValue<int>());
        await pool.DisposeAsync(); Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }
}
