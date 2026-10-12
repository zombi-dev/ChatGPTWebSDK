using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpEscapedReplyTests
{
    private sealed class ProgressLog : IProgress<McpToolProgress>
    {
        public List<McpToolProgress> Events { get; } = [];
        public void Report(McpToolProgress value) => Events.Add(value);
    }

    public static IEnumerable<object[]> ToolReplies()
    {
        foreach (var escaped in new[] { false, true })
        foreach (var chunk in new[] { 1, 2, 3, 7, 17, 4096 })
        foreach (var history in new[] { "fresh", "follow-up", "mixed", "resubmitted" })
            yield return [escaped, chunk, history];
    }

    [Theory, MemberData(nameof(ToolReplies))]
    public async Task Escaped_tool_requests_execute_and_stay_hidden_across_history_and_stream_boundaries(bool escaped, int chunk, string history)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        var progress = new ProgressLog();
        var toolTurn = history == "fresh" ? 1 : 2;
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index switch
        {
            _ when index < toolTurn => (history == "mixed" ? "" : "[[final:" + McpBridgeTests.Nonce(body) + "]]") + "Ready",
            _ when index == toolTurn => (escaped ? " \n<escape>\n" : "") + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) + (escaped ? " \n</escape>\n" : ""),
            _ => "[[final:" + McpBridgeTests.Nonce(body) + "]]5"
        }, chunk);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], Progress = progress });
        var scope = new ConversationScope("account", "alice");
        string? previous = null;
        if (history != "fresh") previous = (await client.SendAsync(scope, new WebTurnRequest
        { Model = "gpt-6", Messages = [WebInputMessage.User("Hello")], UseMcp = history != "mixed" })).Response.Id;
        var request = new WebTurnRequest { Model = "gpt-6", Messages = [WebInputMessage.User("Add 2 and 3")] };
        if (history == "resubmitted") request = await new OpenAiWebAdapter(client).PrepareChatAsync(scope,
            JsonNode.Parse("""{"model":"gpt-6","messages":[{"role":"user","content":"Hello"},{"role":"assistant","content":"Ready"},{"role":"user","content":"Add 2 and 3"}]}""")!.AsObject());

        var events = new List<WebChatEvent>();
        await foreach (var item in client.StreamAsync(scope, request)) events.Add(item);

        Assert.Equal("5", string.Concat(events.Select(e => e.Update.Delta)));
        Assert.Equal("5", events[^1].Response!.Text);
        Assert.Equal(previous, events[^1].Response!.PreviousResponseId);
        Assert.All(events, e => { Assert.DoesNotContain("[[mcp:", e.Update.Text); Assert.DoesNotContain("<escape>", e.Update.Text); });
        Assert.Single(server.Calls);
        Assert.Equal(["executing", "completed"], progress.Events.Select(e => e.Status));
        Assert.Equal(toolTurn + 1, handler.Turns.Count);
        Assert.Equal("assistant-" + toolTurn, handler.Turns[^1].Body["parent_message_id"]!.GetValue<string>());
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(scope));
        var state = await client.GetStateAsync(scope);
        Assert.False(state.RequiresReconciliation);
        Assert.True(Assert.Single(state.McpExecutions).Delivered);
        Assert.Equal(history == "fresh" ? 2 : 4, state.VisibleHistory!.Count);
    }

    public static IEnumerable<object[]> InvalidReplies()
    {
        foreach (var invalid in new[] { "literal", "json", "arguments", "nonce", "closing-nonce", "wrapper-end", "tool-end", "trailing", "inner-trailing" })
        foreach (var repairs in new[] { 0, 1 }) yield return [invalid, repairs];
    }

    [Theory, MemberData(nameof(InvalidReplies))]
    public async Task Invalid_escaped_blocks_are_rejected_before_execution_and_can_use_bounded_repair(string invalid, int repairs)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        var progress = new ProgressLog();
        handler.StreamFactory = (body, index) =>
        {
            var nonce = McpBridgeTests.Nonce(body);
            var stale = nonce[..^1] + (nonce[^1] == '0' ? '1' : '0');
            var reply = McpBridgeTests.ToolReply(invalid == "nonce" && index == 1 ? stale : nonce);
            if (index == 1) reply = invalid switch
            {
                "literal" => McpBridgeTests.ToolReply(nonce, "JSON_ARRAY"),
                "json" => McpBridgeTests.ToolReply(nonce, "{"),
                "arguments" => McpBridgeTests.ToolReply(nonce, """[{"id":"call-1","server":"calculator","name":"add","arguments":{"a":2}}]"""),
                "closing-nonce" => reply.Replace("[[/mcp:" + nonce + "]]", "[[/mcp:" + stale + "]]", StringComparison.Ordinal),
                "tool-end" => reply[..reply.IndexOf("[[/mcp:", StringComparison.Ordinal)],
                "inner-trailing" => reply + "extra",
                _ => reply
            };
            var text = index > 2 ? "[[final:" + nonce + "]]5" : "<escape>" + reply +
                (invalid == "wrapper-end" && index == 1 ? "" : "</escape>") + (invalid == "trailing" && index == 1 ? "extra" : "");
            return McpBridgeTests.Stream(body, index, text, 1);
        };
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], MaxProtocolRepairs = repairs, Progress = progress });
        var scope = new ConversationScope("account", "alice");
        if (repairs == 0)
        {
            var error = await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(scope, "Add", "gpt-6"));
            Assert.Equal(invalid == "arguments" ? "mcp_invalid_arguments" : "mcp_invalid_call", error.Code);
            Assert.Empty(server.Calls); Assert.Empty(progress.Events); Assert.Single(handler.Turns);
        }
        else
        {
            Assert.Equal("5", (await client.SendAsync(scope, "Add", "gpt-6")).Text);
            Assert.Single(server.Calls); Assert.Equal(3, handler.Turns.Count);
            Assert.Equal("protocol_repair", handler.Turns[1].Body["messages"]![0]!["metadata"]!["sdk_mcp_kind"]!.GetValue<string>());
            Assert.True(handler.Turns[1].Body["messages"]![0]!["metadata"]!["is_visually_hidden_from_conversation"]!.GetValue<bool>());
        }
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(scope));
        Assert.False((await client.GetStateAsync(scope)).RequiresReconciliation);
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public async Task Repeated_escaped_literals_stop_at_the_repair_budget_without_running_tools(int repairs)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index,
            "<escape>" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body), "JSON_ARRAY") + "</escape>", 1);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], MaxProtocolRepairs = repairs });
        var scope = new ConversationScope("account", "alice");
        Assert.Equal("mcp_invalid_call", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(scope, "Add", "gpt-6"))).Code);
        Assert.Equal(repairs + 1, handler.Turns.Count); Assert.Empty(server.Calls);
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(scope));
    }

    [Theory, InlineData("<"), InlineData("<escape>"), InlineData("<escape></escape>"), InlineData("<escape>Hello</escape>"),
        InlineData("<escape>See [[mcp:example]] in this explanation.</escape>"), InlineData("<escape>[[final:example]]literal</escape>")]
    public async Task Escaped_ordinary_text_is_preserved_without_tool_execution(string text)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, text, 1);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        var events = new List<WebChatEvent>();
        await foreach (var item in client.StreamAsync(new("account", "alice"), new() { Model = "gpt-6", Messages = [WebInputMessage.User("Explain")] })) events.Add(item);
        Assert.Equal(text, string.Concat(events.Select(e => e.Update.Delta)));
        Assert.Equal(text, events[^1].Response!.Text); Assert.Empty(server.Calls); Assert.Single(handler.Turns);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task A_tool_block_quoted_in_prose_or_an_explicit_final_answer_is_not_executed(bool finalPrefix)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        string expected = "";
        handler.StreamFactory = (body, index) =>
        {
            var nonce = McpBridgeTests.Nonce(body);
            expected = (finalPrefix ? "" : "Example: ") + "<escape>" + McpBridgeTests.ToolReply(nonce) + "</escape>";
            return McpBridgeTests.Stream(body, index, (finalPrefix ? "[[final:" + nonce + "]]" : "") + expected, 1);
        };
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        var response = await client.SendAsync(new("account", "alice"), "Explain", "gpt-6");
        Assert.Equal(expected, response.Text);
        Assert.Empty(server.Calls); Assert.Single(handler.Turns);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Escaped_tool_calls_keep_the_application_approval_policy(bool approve)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        var progress = new ProgressLog();
        int approvals = 0;
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1
            ? "<escape>" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) + "</escape>"
            : "[[final:" + McpBridgeTests.Nonce(body) + "]]Done", 1);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        {
            Servers = [new() { Label = "calculator", Client = server, RequireApproval = true }], Progress = progress,
            ApproveToolCall = (_, _) => { approvals++; return ValueTask.FromResult(approve); }
        });
        Assert.Equal("Done", (await client.SendAsync(new("account", "alice"), "Add", "gpt-6")).Text);
        Assert.Equal(1, approvals); Assert.Equal(approve ? 1 : 0, server.Calls.Count);
        Assert.Equal(approve ? new[] { "executing", "completed" } : ["denied"], progress.Events.Select(e => e.Status));
    }

    [Fact]
    public async Task An_escaped_three_call_batch_executes_each_tool_once()
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        var progress = new ProgressLog();
        var batch = new JsonArray();
        for (int i = 1; i <= 3; i++) batch.Add(new JsonObject
        { ["id"] = "call-" + i, ["server"] = "calculator", ["name"] = "add", ["arguments"] = new JsonObject { ["a"] = 2, ["b"] = 3 } });
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1
            ? "<escape>" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body), batch.ToJsonString()) + "</escape>"
            : "[[final:" + McpBridgeTests.Nonce(body) + "]]15", 1);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }], Progress = progress });
        var scope = new ConversationScope("account", "alice");
        Assert.Equal("15", (await client.SendAsync(scope, "Add three times", "gpt-6")).Text);
        Assert.Equal(3, server.Calls.Count); Assert.Equal(6, progress.Events.Count);
        Assert.All((await client.GetStateAsync(scope)).McpExecutions, e => { Assert.True(e.Delivered); Assert.Equal("completed", e.Status); });
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(scope));
    }

    [Fact]
    public async Task An_uncertain_escaped_call_is_recorded_and_never_replayed()
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server { Run = (_, _, _) => throw new IOException("Synthetic unknown outcome") };
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index,
            "<escape>" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) + "</escape>", 1);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        var scope = new ConversationScope("account", "alice");
        for (int attempt = 0; attempt < 2; attempt++)
            Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(scope, "Add", "gpt-6"))).Code);
        Assert.Single(server.Calls); Assert.Single(handler.Turns);
        Assert.Equal("uncertain", Assert.Single(await client.GetPendingMcpToolCallsAsync(scope)).Status);
    }
}
