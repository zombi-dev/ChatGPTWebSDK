using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpPolicyTests
{
    private static ConversationScope Scope => new("account", "alice");
    private static WebTurnRequest Request => new() { Model = "fixture-model", Messages = [WebInputMessage.User("Use the calculator")] };
    private static ChatGptWebClient Client(FakeWebHandler handler, McpConversationOptions options, IConversationStore? store = null) => new(handler.Client().Transport, store ?? new InMemoryConversationStore(), options);

    [Theory, InlineData("timeout"), InlineData("cancellation"), InlineData("oversize")]
    public async Task Unconfirmed_results_are_never_executed_twice_and_the_error_preserves_the_call(string kind)
    {
        using var handler = new FakeWebHandler(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpBridgeTests.Server { Run = async (_, _, ct) =>
        {
            started.SetResult();
            if (kind == "oversize") return new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('x', 1000) }) };
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return new();
        } };
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)));
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }], ToolTimeout = TimeSpan.FromMilliseconds(100), MaxToolResultCharacters = 128 });
        using var cancellation = new CancellationTokenSource(); var task = client.SendAsync(Scope, Request, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind == "cancellation") { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); }
        else Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => task)).Code);
        Assert.Single(await client.GetPendingMcpToolCallsAsync(Scope));
        Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, Request))).Code);
        Assert.Single(server.Calls); Assert.Single(handler.Turns);
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task Server_allowlists_prevent_execution_of_unlisted_tools(bool allowed)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server, AllowedTools = allowed ? ["add"] : [] }] });
        if (allowed) Assert.Equal("5", (await client.SendAsync(Scope, Request)).Text);
        else Assert.Equal("mcp_tool_denied", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, Request))).Code);
        Assert.Equal(allowed ? 1 : 0, server.Calls.Count);
    }

    [Theory, InlineData("unregistered"), InlineData("denied"), InlineData("endpoint"), InlineData("function"), InlineData("filter"), InlineData("approval"), InlineData("headers")]
    public async Task Invalid_API_MCP_declarations_are_rejected_before_discovery_or_generation(string kind)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }], IsServerAllowed = (scope, label) => kind != "denied" });
        var tool = new JsonObject { ["type"] = "mcp", ["server_label"] = "calculator", ["require_approval"] = "never" };
        switch (kind)
        {
            case "unregistered": tool["server_label"] = "private"; break;
            case "endpoint": tool["server_url"] = "https://wrong.example/mcp"; break;
            case "function": tool["type"] = "function"; break;
            case "filter": tool["allowed_tools"] = false; break;
            case "approval": tool["require_approval"] = "unknown"; break;
            case "headers": tool["headers"] = new JsonObject { ["Authorization"] = "private" }; break;
        }
        var body = new JsonObject { ["model"] = "fixture-model", ["input"] = "Use calculator", ["tools"] = new JsonArray(tool) };
        await Assert.ThrowsAnyAsync<Exception>(() => new OpenAiWebAdapter(client).PrepareResponseAsync(Scope, body));
        Assert.Empty(handler.Turns); Assert.Equal(0, server.Lists); Assert.Empty(server.Calls);
    }

    [Theory, InlineData("always", true), InlineData("never", false), InlineData("filter-always", true), InlineData("filter-never", false), InlineData("read-only", true)]
    public async Task Responses_approval_settings_are_applied_to_the_exact_selected_tool(string policy, bool expectedApproval)
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server(); int approvals = 0;
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }], ApproveToolCall = (call, ct) => { approvals++; return ValueTask.FromResult(true); } });
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        JsonNode approval = policy switch
        {
            "filter-always" => JsonNode.Parse("{\"always\":{\"tool_names\":[\"add\"]}}")!, "filter-never" => JsonNode.Parse("{\"never\":{\"tool_names\":[\"add\"]}}")!,
            "read-only" => JsonNode.Parse("{\"always\":{\"read_only\":true}}")!, _ => JsonValue.Create(policy)!
        };
        var body = new JsonObject { ["model"] = "fixture-model", ["input"] = "Use calculator", ["tools"] = new JsonArray(new JsonObject
        { ["type"] = "mcp", ["server_label"] = "calculator", ["require_approval"] = approval }) };
        Assert.Equal("5", (await client.SendAsync(Scope, await new OpenAiWebAdapter(client).PrepareResponseAsync(Scope, body))).Text);
        Assert.Equal(expectedApproval ? 1 : 0, approvals); Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Concurrent_requests_on_one_scope_cannot_interleave_their_tool_rounds()
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server { Run = async (_, _, ct) => { await Task.Delay(25, ct); return new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "5" }) }; } };
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index % 2 == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }] });
        await Task.WhenAll(client.SendAsync(Scope, Request), client.SendAsync(Scope, Request));
        Assert.Equal(4, handler.Turns.Count); Assert.Equal(2, server.Calls.Count);
        for (int index = 1; index < 4; index++) Assert.Equal("assistant-" + index, handler.Turns[index].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal(4, (await client.GetStateAsync(Scope)).VisibleHistory!.Count);
    }

    [Fact]
    public async Task Multiple_servers_with_colliding_tool_names_route_by_server_label()
    {
        using var handler = new FakeWebHandler(); var first = new McpBridgeTests.Server(); var second = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body),
            "[{\"id\":\"one\",\"server\":\"first\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}},{\"id\":\"two\",\"server\":\"second\",\"name\":\"add\",\"arguments\":{\"a\":7,\"b\":8}}]") : "[[final:" + McpBridgeTests.Nonce(body) + "]]20");
        var client = Client(handler, new() { Servers = [new() { Label = "first", Client = first }, new() { Label = "second", Client = second }] });
        Assert.Equal("20", (await client.SendAsync(Scope, Request)).Text);
        Assert.Equal(2, first.Calls.Single().Arguments["a"]!.GetValue<int>()); Assert.Equal(7, second.Calls.Single().Arguments["a"]!.GetValue<int>());
    }

    [Fact]
    public async Task Temporary_tool_arguments_results_and_visible_transcript_never_reach_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-mcp-private-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
            handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]private-marker-2026");
            var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }] }, new FileConversationStore(directory));
            await client.SendAsync(Scope, new() { Model = "fixture-model", Messages = [WebInputMessage.User("private-marker-2026")], TemporaryChat = true });
            var disk = string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText));
            Assert.DoesNotContain("private-marker-2026", disk); Assert.DoesNotContain("conversation-1", disk); Assert.DoesNotContain("calculator", disk);
            await client.UpdateConversationAsync(Scope, delete: true);
            var state = await client.GetStateAsync(Scope); Assert.Empty(state.McpExecutions); Assert.Null(state.VisibleHistory);
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task Unknown_tool_recovery_preserves_the_last_public_response_id()
    {
        using var handler = new FakeWebHandler(); var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 2 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        var client = Client(handler, new() { Servers = [new() { Label = "calculator", Client = server }] });
        var first = await client.SendAsync(Scope, Request);
        server.Run = (_, _, _) => throw new IOException("unconfirmed");
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, Request));
        Assert.Equal(first.Response.Id, (await client.GetStateAsync(Scope)).LastResponseId);
        var pending = Assert.Single(await client.GetPendingMcpToolCallsAsync(Scope));
        await client.ResolveMcpToolCallAsync(Scope, pending.Id, new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "5" }) });
        var result = await client.SendAsync(Scope, new() { Model = Request.Model, Messages = Request.Messages, PreviousResponseId = first.Response.Id });
        Assert.Equal(first.Response.Id, result.Response.PreviousResponseId); Assert.Single(server.Calls);
    }
}
