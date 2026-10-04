using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace ChatGPTWebSdk.Tests;

public sealed class McpScopeTests
{
    private static ConversationScope Scope(string thread = "research", string user = "alice", string account = "account") => new(account, user, thread);
    private static McpServerConfiguration Config(string label, McpBridgeTests.Server server) => new() { Label = label, Client = server };
    private static WebTurnRequest Request(McpScopeOptions? mcp = null, string context = "normal") => new()
    { Model = "fixture-model", Messages = [WebInputMessage.User("Use available tools")], Mcp = mcp, TemporaryChat = context == "temporary", ProjectId = context == "project" ? "g-p-project" : null };
    private static string Text(JsonObject body) => body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>();
    private static string AutoReply(JsonObject body, int index)
    {
        var text = Text(body);
        var match = Regex.Match(text, "Only request these client-implemented functions:\\n(.*?)\\n\\nUser request:", RegexOptions.Singleline);
        var nonce = McpBridgeTests.Nonce(body);
        if (match.Success && JsonNode.Parse(match.Groups[1].Value) is JsonArray { Count: > 0 } tools)
        {
            var calls = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["id"] = "call-" + index + "-" + t!["server"]!.GetValue<string>(),
                ["server"] = t["server"]!.DeepClone(), ["name"] = t["name"]!.DeepClone(), ["arguments"] = new JsonObject { ["a"] = 2, ["b"] = 3 } }).ToArray());
            return McpBridgeTests.Stream(body, index, McpBridgeTests.ToolReply(nonce, calls.ToJsonString()));
        }
        return McpBridgeTests.Stream(body, index, (nonce.Length == 0 ? "" : "[[final:" + nonce + "]]") + "done");
    }
    private static ChatGPTWebRuntime Runtime(FakeWebHandler handler, McpConversationOptions? mcp = null) => new(new()
    {
        Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic" }),
        AccountId = "account", UserId = "alice", HttpClient = new(handler, false), ConversationStore = new InMemoryConversationStore(), Endpoints = new(), Mcp = mcp
    });

    public static IEnumerable<object[]> Inheritance()
    {
        foreach (var global in new[] { false, true })
        foreach (var chat in new[] { "none", "merge", "replace" })
        foreach (var message in new[] { "none", "merge", "replace" })
        foreach (var context in new[] { "normal", "project", "temporary" }) yield return [global, chat, message, context];
    }
    [Theory, MemberData(nameof(Inheritance))]
    public async Task Scopes_inherit_or_replace_servers_for_the_entire_logical_turn(bool global, string chatMode, string messageMode, string context)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply };
        var initial = new McpBridgeTests.Server(); var chat = new McpBridgeTests.Server(); var message = new McpBridgeTests.Server();
        var client = global ? new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new() { Servers = [Config("initial", initial)] }) : handler.Client();
        if (chatMode != "none") client.SetChatMcp(Scope(), new() { Servers = [Config("chat", chat)], InheritServers = chatMode == "merge" });
        var mcp = messageMode == "none" ? null : new McpScopeOptions { Servers = [Config("message", message)], InheritServers = messageMode == "merge" };
        Assert.Equal("done", (await client.SendAsync(Scope(), Request(mcp, context))).Text);
        Assert.Equal(global && chatMode != "replace" && messageMode != "replace" ? 1 : 0, initial.Calls.Count);
        Assert.Equal(chatMode != "none" && messageMode != "replace" ? 1 : 0, chat.Calls.Count);
        Assert.Equal(messageMode != "none" ? 1 : 0, message.Calls.Count);
        Assert.All(handler.Turns, turn =>
        {
            if (context == "temporary") Assert.True(turn.Body["history_and_training_disabled"]!.GetValue<bool>());
            if (context == "project") Assert.Equal("g-p-project", turn.Body["gizmo_id"]!.GetValue<string>());
        });
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(Scope()));
    }

    public static IEnumerable<object[]> OfficialClients()
    {
        foreach (var kind in new[] { "chat", "responses" })
        foreach (var streaming in new[] { false, true })
        foreach (var synchronous in new[] { false, true })
        foreach (var initial in new[] { false, true }) yield return [kind, streaming, synchronous, initial];
    }
    private static async Task<string> Complete(ChatGPTWebRuntime runtime, string thread, string kind, bool streaming, bool synchronous)
    {
        var client = runtime.CreateClient(threadId: thread);
        if (kind == "chat")
        {
            var chat = client.GetChatClient("fixture-model");
            if (!streaming) return synchronous ? chat.CompleteChat("Use available tools").Value.Content[0].Text : (await chat.CompleteChatAsync("Use available tools")).Value.Content[0].Text;
            var text = "";
            if (synchronous) foreach (var update in chat.CompleteChatStreaming("Use available tools")) foreach (var part in update.ContentUpdate) text += part.Text;
            else await foreach (var update in chat.CompleteChatStreamingAsync("Use available tools")) foreach (var part in update.ContentUpdate) text += part.Text;
            return text;
        }
        var responses = client.GetResponsesClient();
        if (!streaming) return synchronous ? responses.CreateResponse("fixture-model", "Use available tools").Value.GetOutputText() : (await responses.CreateResponseAsync("fixture-model", "Use available tools")).Value.GetOutputText();
        var answer = "";
        if (synchronous) foreach (var update in responses.CreateResponseStreaming("fixture-model", "Use available tools")) { if (update is StreamingResponseOutputTextDeltaUpdate delta) answer += delta.Delta; }
        else await foreach (var update in responses.CreateResponseStreamingAsync("fixture-model", "Use available tools")) if (update is StreamingResponseOutputTextDeltaUpdate delta) answer += delta.Delta;
        return answer;
    }
    [Theory, MemberData(nameof(OfficialClients))]
    public async Task Message_scope_is_consumed_by_one_official_generation_and_restores_the_chat_default(string kind, bool streaming, bool synchronous, bool initial)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply };
        var global = new McpBridgeTests.Server(); var chat = new McpBridgeTests.Server(); var message = new McpBridgeTests.Server();
        using var runtime = Runtime(handler, initial ? new() { Servers = [Config("calculator", global)] } : null);
        runtime.SetChatMcp(new() { Servers = [Config("calculator", chat)] }, "research");
        using (runtime.UseMessageMcp(new() { Servers = [Config("calculator", message)] }))
        {
            Assert.Equal("done", await Complete(runtime, "research", kind, streaming, synchronous));
            Assert.Equal("done", await Complete(runtime, "research", kind, streaming, synchronous));
        }
        Assert.Equal("done", await Complete(runtime, "research", kind, streaming, synchronous));
        Assert.Single(message.Calls); Assert.Equal(2, chat.Calls.Count); Assert.Empty(global.Calls);
        Assert.Equal(6, handler.Turns.Count); Assert.Same(chat, runtime.GetChatMcp("research")!.Servers.Single().Client);
    }

    [Theory, InlineData("account"), InlineData("user"), InlineData("thread")]
    public async Task Chat_bindings_do_not_cross_account_user_or_thread_boundaries(string boundary)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server(); var client = handler.Client();
        client.SetChatMcp(Scope(), new() { Servers = [Config("calculator", server)] });
        var other = boundary switch { "account" => Scope(account: "other-account"), "user" => Scope(user: "bob"), _ => Scope("other-thread") };
        await client.SendAsync(other, Request()); Assert.Empty(server.Calls); Assert.Null(client.GetChatMcp(other));
        await client.SendAsync(Scope(), Request()); Assert.Single(server.Calls);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task A_message_override_expires_and_does_not_leave_the_old_manifest_active(bool initial)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var global = new McpBridgeTests.Server(); var message = new McpBridgeTests.Server();
        var client = initial ? new(handler.Client().Transport, new InMemoryConversationStore(), new McpConversationOptions { Servers = [Config("calculator", global)] }) : handler.Client();
        await client.SendAsync(Scope(), Request(new() { Servers = [Config("calculator", message)] }));
        await client.SendAsync(Scope(), Request());
        Assert.Single(message.Calls); Assert.Equal(initial ? 1 : 0, global.Calls.Count);
        Assert.Contains("Only request these client-implemented functions:", Text(handler.Turns[2].Body));
        if (!initial) Assert.Contains("Only request these client-implemented functions:\n[]", Text(handler.Turns[2].Body));
        Assert.Null(client.GetChatMcp(Scope()));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Excluding_a_server_affects_only_the_chosen_scope(bool messageOnly)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var a = new McpBridgeTests.Server(); var b = new McpBridgeTests.Server();
        var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new() { Servers = [Config("a", a), Config("b", b)] });
        var excluded = new McpScopeOptions { ExcludedServers = ["a"] };
        if (!messageOnly) client.SetChatMcp(Scope(), excluded);
        await client.SendAsync(Scope(), Request(messageOnly ? excluded : null));
        Assert.Empty(a.Calls); Assert.Single(b.Calls);
        if (!messageOnly) client.SetChatMcp(Scope(), null);
        await client.SendAsync(Scope(), Request());
        Assert.Single(a.Calls); Assert.Equal(2, b.Calls.Count);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task An_empty_replacement_disables_inherited_servers_and_can_be_cleared(bool messageOnly)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server();
        var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new() { Servers = [Config("calculator", server)] });
        var empty = new McpScopeOptions { InheritServers = false };
        if (!messageOnly) client.SetChatMcp(Scope(), empty);
        await client.SendAsync(Scope(), Request(messageOnly ? empty : null)); Assert.Empty(server.Calls); Assert.Equal(0, server.Lists);
        if (!messageOnly) client.SetChatMcp(Scope(), null);
        await client.SendAsync(Scope(), Request()); Assert.Single(server.Calls);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Caller_mutations_do_not_change_registered_connection_collections(bool chatScope)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server();
        var servers = new List<McpServerConfiguration>(); var tools = new List<string> { "add" }; var headers = new Dictionary<string, string> { ["X-Test"] = "original" };
        servers.Add(Config("calculator", server) with { AllowedTools = tools, Headers = headers });
        using var runtime = Runtime(handler);
        using var messageScope = chatScope ? null : runtime.UseMessageMcp(new() { Servers = servers });
        if (chatScope) runtime.SetChatMcp(new() { Servers = servers }, "research");
        servers.Clear(); tools.Clear(); headers["X-Test"] = "mutated";
        Assert.Equal("done", await Complete(runtime, "research", "chat", false, false)); Assert.Single(server.Calls);
        if (chatScope) Assert.Equal("original", runtime.GetChatMcp("research")!.Servers[0].Headers["X-Test"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Approval_and_permissions_apply_to_scoped_servers_without_disclosing_connection_secrets(bool denied)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server(); int approvals = 0;
        var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        {
            IsServerAllowed = (_, _) => !denied,
            ApproveToolCall = (call, _) => { approvals++; Assert.Same(server, call.Server!.Client); Assert.Equal("synthetic-secret", call.Server.Headers["X-Test"]); Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(call)); return ValueTask.FromResult(true); }
        });
        await client.SendAsync(Scope(), Request(new() { Servers = [Config("calculator", server) with { RequireApproval = true, Headers = new Dictionary<string, string> { ["X-Test"] = "synthetic-secret" } }] }));
        Assert.Equal(denied ? 0 : 1, approvals); Assert.Equal(denied ? 0 : 1, server.Calls.Count);
        Assert.All(handler.Turns, t => Assert.DoesNotContain("synthetic-secret", t.Body.ToJsonString()));
    }

    [Fact]
    public async Task Updating_chat_servers_during_a_tool_round_applies_only_to_the_next_message()
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var first = new McpBridgeTests.Server(); var next = new McpBridgeTests.Server(); var client = handler.Client();
        first.Run = (_, _, _) => { client.SetChatMcp(Scope(), new() { Servers = [Config("calculator", next)] }); return Task.FromResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "5" }) }); };
        client.SetChatMcp(Scope(), new() { Servers = [Config("calculator", first)] });
        await client.SendAsync(Scope(), Request()); Assert.Single(first.Calls); Assert.Empty(next.Calls);
        await client.SendAsync(Scope(), Request()); Assert.Single(next.Calls);
    }

    [Fact]
    public async Task Parallel_official_messages_keep_separate_MCP_scopes()
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply, DelayMilliseconds = 15 }; using var runtime = Runtime(handler);
        var a = new McpBridgeTests.Server(); var b = new McpBridgeTests.Server();
        async Task Run(string thread, McpBridgeTests.Server server)
        { using (runtime.UseMessageMcp(new() { Servers = [Config("calculator", server)] })) Assert.Equal("done", await Complete(runtime, thread, "chat", true, false)); }
        await Task.WhenAll(Run("a", a), Run("b", b)); Assert.Single(a.Calls); Assert.Single(b.Calls);
        Assert.Equal("done", await Complete(runtime, "plain", "responses", false, false)); Assert.Equal(5, handler.Turns.Count);
    }

    [Fact]
    public async Task Nested_message_scopes_are_single_use_and_restore_the_pending_outer_scope()
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; using var runtime = Runtime(handler);
        var outer = new McpBridgeTests.Server(); var inner = new McpBridgeTests.Server();
        using (runtime.UseMessageMcp(new() { Servers = [Config("calculator", outer)] }))
        {
            using (runtime.UseMessageMcp(new() { Servers = [Config("calculator", inner)] }))
            {
                await Complete(runtime, "research", "chat", false, false);
                await Complete(runtime, "research", "chat", false, false);
                Assert.Empty(outer.Calls); Assert.Single(inner.Calls);
            }
            await Complete(runtime, "research", "chat", false, false); Assert.Single(outer.Calls);
        }
    }

    [Fact]
    public void Message_scopes_require_ordered_disposal_and_allow_repeated_disposal()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var outer = runtime.UseMessageMcp(new()); var inner = runtime.UseMessageMcp(new());
        Assert.Throws<InvalidOperationException>(() => outer.Dispose()); inner.Dispose(); outer.Dispose(); outer.Dispose();
    }

    [Fact]
    public async Task Model_reads_do_not_consume_a_pending_message_scope()
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; using var runtime = Runtime(handler); var server = new McpBridgeTests.Server();
        using (runtime.UseMessageMcp(new() { Servers = [Config("calculator", server)] }))
        {
            Assert.Single((await runtime.CreateClient().GetOpenAIModelClient().GetModelsAsync()).Value);
            await Complete(runtime, "research", "chat", false, false);
        }
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task An_uncertain_message_only_tool_can_be_resolved_after_its_scope_expires_without_reconnecting()
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server { Run = (_, _, _) => throw new IOException("synthetic unknown result") };
        var client = handler.Client();
        Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), Request(new() { Servers = [Config("calculator", server)] })))).Code);
        Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), Request()))).Code);
        var pending = Assert.Single(await client.GetPendingMcpToolCallsAsync(Scope()));
        await client.ResolveMcpToolCallAsync(Scope(), pending.Id, new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "confirmed result" }) });
        Assert.Equal("done", (await client.SendAsync(Scope(), Request())).Text);
        Assert.Single(server.Calls); Assert.Equal(1, server.Lists);
        Assert.Contains("confirmed result", Text(handler.Turns[^1].Body));
        Assert.True((await client.GetStateAsync(Scope())).McpExecutions.Single().Delivered);
    }

    [Theory, InlineData("invalid-label"), InlineData("duplicate"), InlineData("excluded"), InlineData("excluded-duplicate"), InlineData("overlap"), InlineData("invalid-url"), InlineData("invalid-header")]
    public void Invalid_scopes_do_not_replace_an_existing_chat_binding(string invalid)
    {
        using var handler = new FakeWebHandler(); var client = handler.Client(); var server = new McpBridgeTests.Server(); var good = Config("calculator", server);
        client.SetChatMcp(Scope(), new() { Servers = [good] });
        var bad = invalid switch
        {
            "invalid-label" => new McpScopeOptions { Servers = [good with { Label = "bad\nlabel" }] },
            "duplicate" => new() { Servers = [good, good] },
            "excluded" => new() { ExcludedServers = ["bad label"] },
            "excluded-duplicate" => new() { ExcludedServers = ["calculator", "calculator"] },
            "overlap" => new() { Servers = [good], ExcludedServers = ["calculator"] },
            "invalid-url" => new() { Servers = [new() { Label = "calculator", Endpoint = new("file:///private") }] },
            _ => new() { Servers = [good with { Headers = new Dictionary<string, string> { ["X-Test"] = "bad\r\nheader" } }] }
        };
        Assert.Throws<ArgumentException>(() => client.SetChatMcp(Scope(), bad)); Assert.Same(server, client.GetChatMcp(Scope())!.Servers.Single().Client); Assert.Empty(handler.Turns);
    }

    [Theory, InlineData("native"), InlineData("responses")]
    public async Task Deleting_a_chat_clears_its_scoped_servers(string api)
    {
        using var handler = new FakeWebHandler { StreamFactory = AutoReply }; using var runtime = Runtime(handler); var server = new McpBridgeTests.Server();
        var thread = "research";
        if (api == "responses") thread = (await runtime.CreateClient().GetConversationClient().CreateConversationAsync(new())).Value.Id;
        runtime.SetChatMcp(new() { Servers = [Config("calculator", server)] }, thread);
        await Complete(runtime, thread, "chat", false, false);
        if (api == "native") await runtime.Web.UpdateConversationAsync(runtime.ResolveScope(runtime.ClientKey, thread), delete: true);
        else await runtime.CreateClient().GetConversationClient().DeleteConversationAsync(thread);
        Assert.Null(runtime.GetChatMcp(thread));
    }

    [Fact]
    public async Task Scoped_connection_secrets_are_not_persisted_and_chat_registration_is_runtime_local()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-scoped-mcp-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler { StreamFactory = AutoReply }; var server = new McpBridgeTests.Server();
            var client = new ChatGptWebClient(handler.Client().Transport, new FileConversationStore(directory));
            client.SetChatMcp(Scope(), new() { Servers = [Config("calculator", server) with { Headers = new Dictionary<string, string> { ["X-Test"] = "synthetic-secret" } }] });
            await client.SendAsync(Scope(), Request());
            foreach (var file in Directory.EnumerateFiles(directory)) Assert.DoesNotContain("synthetic-secret", File.ReadAllText(file));
            var restarted = new ChatGptWebClient(handler.Client().Transport, new FileConversationStore(directory));
            Assert.Null(restarted.GetChatMcp(Scope())); await restarted.SendAsync(Scope(), Request()); Assert.Single(server.Calls);
        }
        finally { TestDirectory.Delete(directory); }
    }
}
