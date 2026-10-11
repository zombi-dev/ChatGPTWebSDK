using System.Net;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpReliabilityTests
{
    [Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(7), InlineData(17), InlineData(64)]
    public async Task Fenced_and_trailing_whitespace_tool_requests_remain_hidden_at_every_stream_boundary(int chunk)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1
            ? "```json\n" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) + " \n```\n"
            : "[[final:" + McpBridgeTests.Nonce(body) + "]]5", chunk);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        var events = new List<WebChatEvent>();
        await foreach (var item in client.StreamAsync(new("account", "alice"), new() { Model = "gpt-6", Messages = [WebInputMessage.User("Add")] })) events.Add(item);
        Assert.Equal("5", string.Concat(events.Select(e => e.Update.Delta))); Assert.Single(server.Calls);
        Assert.All(events, item => Assert.DoesNotContain("[[mcp:", item.Update.Text));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task A_rejected_batch_can_be_repaired_with_the_same_ids_before_any_tool_runs(bool invalidArguments)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index switch
        {
            1 => McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body), invalidArguments ? "[{\"id\":\"call-1\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2}}]" : "{"),
            2 => McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)),
            _ => "[[final:" + McpBridgeTests.Nonce(body) + "]]5"
        });
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        Assert.Equal("5", (await client.SendAsync(new("account", "alice"), "Add", "gpt-6")).Text);
        Assert.Single(server.Calls); Assert.Equal(3, handler.Turns.Count);
        Assert.Equal("protocol_repair", handler.Turns[1].Body["messages"]![0]!["metadata"]!["sdk_mcp_kind"]!.GetValue<string>());
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public async Task Malformed_requests_use_only_the_configured_repair_budget(int repairs)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body), "{"));
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], MaxProtocolRepairs = repairs });
        Assert.Equal("mcp_invalid_call", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(new("account", "alice"), "Add", "gpt-6"))).Code);
        Assert.Equal(repairs + 1, handler.Turns.Count); Assert.Empty(server.Calls);
    }

    private sealed class BrokenProgress : IProgress<McpToolProgress>
    { public void Report(McpToolProgress value) => throw new InvalidOperationException("Synthetic observer failure"); }

    [Fact]
    public async Task A_broken_progress_observer_cannot_change_a_confirmed_tool_outcome()
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], Progress = new BrokenProgress() });
        Assert.Equal("5", (await client.SendAsync(new("account", "alice"), "Add", "gpt-6")).Text); Assert.Single(server.Calls);
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(new("account", "alice")));
    }

    [Theory, InlineData(404, true), InlineData(408, true), InlineData(429, true), InlineData(500, true), InlineData(502, true), InlineData(503, true), InlineData(504, true),
        InlineData(400, false), InlineData(401, false), InlineData(403, false), InlineData(422, false)]
    public void Discovery_retries_transient_failures_and_leaves_authentication_errors_to_the_application(int status, bool transient) =>
        Assert.Equal(transient, McpConnectionPool.IsTransient(new HttpRequestException("Synthetic", null, (HttpStatusCode)status)));

    [Theory, InlineData("account"), InlineData("user"), InlineData("thread"), InlineData("endpoint"), InlineData("authorization"), InlineData("transport"), InlineData("label"), InlineData("reconnection")]
    public void Reusable_connections_separate_scopes_and_connection_credentials(string change)
    {
        var scope = new ConversationScope("account", "alice", "chat");
        var server = new McpServerConfiguration { Label = "tools", Endpoint = new("https://fixture.invalid/mcp"), Headers = new Dictionary<string, string> { ["Authorization"] = "synthetic-one" } };
        var before = McpConnectionPool.Key(scope, server);
        scope = change switch { "account" => new("other", "alice", "chat"), "user" => new("account", "bob", "chat"), "thread" => new("account", "alice", "other"), _ => scope };
        server = change switch
        {
            "endpoint" => server with { Endpoint = new("https://fixture.invalid/other") }, "authorization" => server with { Headers = new Dictionary<string, string> { ["Authorization"] = "synthetic-two" } },
            "transport" => server with { Transport = McpWebTransport.Sse }, "label" => server with { Label = "other" }, "reconnection" => server with { MaxReconnectionAttempts = 0 }, _ => server
        };
        Assert.NotEqual(before, McpConnectionPool.Key(scope, server)); Assert.DoesNotContain("synthetic", before);
    }

    [Fact]
    public void Equivalent_header_order_and_casing_do_not_duplicate_connections()
    {
        var scope = new ConversationScope("account", "alice");
        var a = new McpServerConfiguration { Label = "tools", Endpoint = new("https://fixture.invalid/mcp"), Headers = new Dictionary<string, string> { ["Authorization"] = "synthetic", ["X-Tenant"] = "one" } };
        var b = a with { Headers = new Dictionary<string, string> { ["x-tenant"] = "one", ["authorization"] = "synthetic" } };
        Assert.Equal(McpConnectionPool.Key(scope, a), McpConnectionPool.Key(scope, b));
    }

    [Theory, InlineData("connect"), InlineData("idle"), InlineData("capacity"), InlineData("retry"), InlineData("delay"), InlineData("repairs"), InlineData("resume"), InlineData("resumeDelay")]
    public void Invalid_reuse_and_recovery_settings_fail_before_generation(string invalid)
    {
        using var handler = new FakeWebHandler();
        var server = new McpServerConfiguration
        {
            Label = "tools", Client = new McpBridgeTests.Server(),
            MaxReconnectionAttempts = invalid == "resume" ? 11 : 2,
            ReconnectionInterval = invalid == "resumeDelay" ? TimeSpan.FromSeconds(-1) : TimeSpan.FromMilliseconds(250)
        };
        Assert.Throws<ArgumentException>(() => new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        {
            Servers = [server], ConnectionTimeout = invalid == "connect" ? TimeSpan.Zero : TimeSpan.FromSeconds(30),
            ConnectionIdleTimeout = invalid == "idle" ? TimeSpan.Zero : TimeSpan.FromMinutes(2), MaxConnections = invalid == "capacity" ? 0 : 32,
            DiscoveryRetries = invalid == "retry" ? 4 : 1, DiscoveryRetryDelay = invalid == "delay" ? TimeSpan.FromSeconds(-1) : TimeSpan.Zero,
            MaxProtocolRepairs = invalid == "repairs" ? 4 : 1
        }));
        Assert.Empty(handler.Turns);
    }
}
