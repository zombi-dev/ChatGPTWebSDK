using System.Net;
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

public sealed class McpBridgeTests
{
    internal sealed class Server : IMcpWebServer
    {
        public List<(string Name, JsonObject Arguments)> Calls { get; } = [];
        public int Lists { get; private set; }
        public Func<string, JsonObject, CancellationToken, Task<JsonObject>>? Run { get; set; }
        public IReadOnlyList<McpWebTool> Tools { get; set; } = [new("add", "Adds two integers", JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}},\"required\":[\"a\",\"b\"],\"additionalProperties\":false}")!.AsObject(), true)];
        public Task<IReadOnlyList<McpWebTool>> ListToolsAsync(CancellationToken ct = default) { Lists++; return Task.FromResult(Tools); }
        public Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default)
        {
            Calls.Add((name, (JsonObject)arguments.DeepClone()));
            return Run?.Invoke(name, arguments, ct) ?? Task.FromResult(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (arguments["a"]!.GetValue<int>() + arguments["b"]!.GetValue<int>()).ToString(System.Globalization.CultureInfo.InvariantCulture) }), ["isError"] = false });
        }
    }
    private static ConversationScope Scope(string thread = "default", string user = "alice") => new("account", user, thread);
    internal static string Nonce(JsonObject body) => Regex.Match(body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>(), @"\[\[final:([a-f0-9]{24})\]\]").Groups[1].Value;
    internal static string Stream(JsonObject body, int index, string text, int chunk = 17)
    {
        var conversation = body["conversation_id"]?.GetValue<string>() ?? "conversation-" + index;
        var events = "";
        for (int length = Math.Min(chunk, text.Length); length < text.Length; length += chunk)
            if (!char.IsHighSurrogate(text[length - 1])) events += FakeWebHandler.Event(FakeWebHandler.Snapshot(conversation, "assistant-" + index, text[..length], false));
        return events + FakeWebHandler.Event(FakeWebHandler.Snapshot(conversation, "assistant-" + index, text, true)) + "data: [DONE]\n\n";
    }
    internal static string ToolReply(string nonce, string json = "[{\"id\":\"call-1\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}}]") => "[[mcp:" + nonce + "]]" + json + "[[/mcp:" + nonce + "]]";
    private static ChatGptWebClient Client(FakeWebHandler handler, Server server, IConversationStore? store = null, McpConversationOptions? options = null) =>
        new(handler.Client().Transport, store ?? new InMemoryConversationStore(), options ?? new() { Servers = [new() { Label = "calculator", Client = server }] });
    private static WebTurnRequest Request(string text = "Add 2 and 3", bool temporary = false, string? project = null) => new()
    { Model = "gpt-6", Messages = [WebInputMessage.User(text)], TemporaryChat = temporary, ProjectId = project };

    public static IEnumerable<object[]> Streams()
    {
        foreach (var chunk in new[] { 1, 2, 3, 7, 17, 64 })
        foreach (var text in new[] { "5", "The answer is five.", "日本語 😀", "Line one\nLine two", "JSON {\"value\":5}", "A [[mcp:example]] inside an explanation" })
        foreach (var temporary in new[] { false, true }) yield return [chunk, text, temporary];
    }
    [Theory, MemberData(nameof(Streams))]
    public async Task Tool_exchanges_are_hidden_while_final_text_streams_and_keeps_the_remote_parent(int chunk, string text, bool temporary)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, index == 1 ? ToolReply(Nonce(body)) : "[[final:" + Nonce(body) + "]]" + text, chunk);
        var client = Client(handler, server); var events = new List<WebChatEvent>();
        await foreach (var item in client.StreamAsync(Scope(), Request(temporary: temporary))) events.Add(item);
        Assert.Single(server.Calls); Assert.Equal(2, handler.Turns.Count);
        Assert.Equal(text, string.Concat(events.Select(e => e.Update.Delta)));
        Assert.Equal(text, events[^1].Response!.Text); Assert.Equal("conversation-1", events[^1].Response!.ConversationId);
        Assert.All(events, e => Assert.DoesNotContain("[[final:", e.Update.Text));
        Assert.Equal("assistant-1", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal("conversation-1", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.True(handler.Turns[1].Body["messages"]![0]!["metadata"]!["is_visually_hidden_from_conversation"]!.GetValue<bool>());
        var state = await client.GetStateAsync(Scope());
        Assert.Equal(4, state.History.Count); Assert.Equal(2, state.VisibleHistory!.Count); Assert.Single(state.Responses);
        Assert.Equal("Add 2 and 3", state.Responses[0].Input.Single().Text);
        Assert.Single(state.McpExecutions); Assert.True(state.McpExecutions[0].Delivered); Assert.False(state.RequiresReconciliation);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Official_chat_and_responses_clients_receive_only_the_final_answer(bool streaming)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, index % 2 == 1 ? ToolReply(Nonce(body)) : "[[final:" + Nonce(body) + "]]5");
        using var runtime = new ChatGPTWebRuntime(new() { Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic" }),
            AccountId = "account", UserId = "alice", HttpClient = new(handler, false), ConversationStore = new InMemoryConversationStore(), Endpoints = new(),
            Mcp = new() { Servers = [new() { Label = "calculator", Client = server }] } });
        var chat = runtime.CreateClient(threadId: "chat").GetChatClient("gpt-6");
        var responses = runtime.CreateClient(threadId: "responses").GetResponsesClient();
        var chatText = ""; var responseText = "";
        if (streaming)
        {
            await foreach (var item in chat.CompleteChatStreamingAsync("Add 2 and 3")) foreach (var part in item.ContentUpdate) chatText += part.Text;
            await foreach (var item in responses.CreateResponseStreamingAsync("gpt-6", "Add 2 and 3")) if (item is StreamingResponseOutputTextDeltaUpdate delta) responseText += delta.Delta;
        }
        else
        {
            chatText = (await chat.CompleteChatAsync("Add 2 and 3")).Value.Content[0].Text;
            responseText = (await responses.CreateResponseAsync("gpt-6", "Add 2 and 3")).Value.GetOutputText();
        }
        Assert.Equal("5", chatText); Assert.Equal("5", responseText); Assert.Equal(2, server.Calls.Count);
    }

    [Fact]
    public async Task Visible_history_resubmissions_and_previous_response_ids_continue_without_resending_tool_messages()
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, index == 1 ? ToolReply(Nonce(body)) : "[[final:" + Nonce(body) + "]]5");
        var client = Client(handler, server); var first = await client.SendAsync(Scope(), Request());
        var adapter = new OpenAiWebAdapter(client);
        var body = JsonNode.Parse("{\"model\":\"gpt-6\",\"messages\":[{\"role\":\"user\",\"content\":\"Add 2 and 3\"},{\"role\":\"assistant\",\"content\":\"5\"},{\"role\":\"user\",\"content\":\"Remember it\"}]}")!.AsObject();
        var next = await client.SendAsync(Scope(), await adapter.PrepareChatAsync(Scope(), body));
        Assert.Equal(first.Response.Id, next.Response.PreviousResponseId); Assert.Single(handler.Turns[2].Body["messages"]!.AsArray());
        Assert.Equal("assistant-2", handler.Turns[2].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal(4, (await client.GetStateAsync(Scope())).VisibleHistory!.Count);
        Assert.Equal("Add 2 and 3", (await client.GetResponseAsync(Scope(), first.Response.Id)).Input.Single().Text);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Project_and_temporary_context_is_preserved_on_every_tool_result_turn(bool temporary)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, index == 1 ? ToolReply(Nonce(body)) : "[[final:" + Nonce(body) + "]]5");
        var client = Client(handler, server); await client.SendAsync(Scope(), Request(temporary: temporary, project: temporary ? null : "g-p-project"));
        foreach (var turn in handler.Turns)
            if (temporary) Assert.True(turn.Body["history_and_training_disabled"]!.GetValue<bool>()); else Assert.Equal("g-p-project", turn.Body["gizmo_id"]!.GetValue<string>());
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task Approval_callback_can_allow_or_deny_without_exposing_tool_protocol(bool approve)
    {
        using var handler = new FakeWebHandler(); var server = new Server(); int approvals = 0;
        handler.StreamFactory = (body, index) => Stream(body, index, index == 1 ? ToolReply(Nonce(body)) : "[[final:" + Nonce(body) + "]]Done");
        var client = Client(handler, server, options: new() { Servers = [new() { Label = "calculator", Client = server, RequireApproval = true }],
            ApproveToolCall = (context, ct) => { approvals++; Assert.Equal("alice", context.Scope.UserId); return ValueTask.FromResult(approve); } });
        var result = await client.SendAsync(Scope(), Request());
        Assert.Equal("Done", result.Text); Assert.Equal(1, approvals); Assert.Equal(approve ? 1 : 0, server.Calls.Count);
        Assert.Equal(!approve, handler.Turns[1].Body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>().Contains("execution was denied"));
    }

    public static IEnumerable<object[]> InvalidCalls()
    {
        yield return ["[]", "mcp_invalid_call"];
        yield return ["{", "mcp_invalid_call"];
        yield return ["{}", "mcp_invalid_call"];
        yield return ["[null]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2}}]", "mcp_invalid_arguments"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3,\"c\":4}}]", "mcp_invalid_arguments"];
        yield return ["[{\"id\":\"x\",\"server\":\"other\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}}]", "mcp_tool_denied"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"remove\",\"arguments\":{\"a\":2,\"b\":3}}]", "mcp_tool_denied"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":[]}]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":null}]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{}}]", "mcp_invalid_call"];
        yield return ["[{\"id\":5,\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{}}]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3},\"extra\":true}]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}},{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}}]", "mcp_invalid_call"];
        yield return ["[{\"id\":\"x\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}},{\"id\":\"y\",\"server\":\"unknown\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}}]", "mcp_tool_denied"];
    }
    [Theory, MemberData(nameof(InvalidCalls))]
    public async Task Invalid_batches_are_rejected_before_any_tool_executes(string json, string code)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, ToolReply(Nonce(body), json));
        var error = await Assert.ThrowsAsync<SdkException>(() => Client(handler, server).SendAsync(Scope(), Request()));
        Assert.Equal(code, error.Code); Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task Unknown_tool_outcome_survives_restart_and_requires_explicit_resolution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-mcp-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler(); var server = new Server { Run = (_, _, _) => throw new IOException("private server failure") };
            handler.StreamFactory = (body, index) => Stream(body, index, ToolReply(Nonce(body)));
            var client = Client(handler, server, new FileConversationStore(directory));
            var error = await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), Request()));
            Assert.Equal("mcp_tool_outcome_unknown", error.Code); Assert.DoesNotContain("private server", error.Message);
            var restarted = Client(handler, server, new FileConversationStore(directory));
            Assert.Equal("mcp_tool_outcome_unknown", (await Assert.ThrowsAsync<SdkException>(() => restarted.SendAsync(Scope(), Request()))).Code);
            Assert.Single(server.Calls); Assert.Single(handler.Turns);
            var pending = Assert.Single(await restarted.GetPendingMcpToolCallsAsync(Scope()));
            await restarted.ResolveMcpToolCallAsync(Scope(), pending.Id, new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "independently confirmed 5" }) });
            handler.StreamFactory = (body, index) => Stream(body, index, "[[final:" + Nonce(body) + "]]5");
            Assert.Equal("5", (await restarted.SendAsync(Scope(), Request("Continue"))).Text);
            Assert.Single(server.Calls); Assert.Empty(await restarted.GetPendingMcpToolCallsAsync(Scope()));
            Assert.Contains("independently confirmed 5", handler.Turns[^1].Body.ToJsonString());
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Theory, InlineData("round"), InlineData("calls"), InlineData("arguments"), InlineData("manifest")]
    public async Task Limits_stop_generation_or_tool_execution_without_retry(string limit)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, ToolReply(Nonce(body), "[{\"id\":\"call-" + index + "\",\"server\":\"calculator\",\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3" + (limit == "arguments" ? ",\"extra\":\"" + new string('x', 200) + "\"" : "") + "}}]"));
        var client = Client(handler, server, options: new() { Servers = [new() { Label = "calculator", Client = server }], MaxToolRounds = 1, MaxToolCalls = 1,
            MaxManifestCharacters = limit == "manifest" ? 128 : 256 * 1024, MaxToolArgumentsCharacters = limit == "arguments" ? 128 : 64 * 1024 });
        var error = await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), Request()));
        Assert.Equal(limit switch { "arguments" => "mcp_arguments_too_large", "manifest" => "mcp_manifest_too_large", _ => "mcp_round_limit" }, error.Code);
        Assert.Equal(limit is "arguments" or "manifest" ? 0 : 1, server.Calls.Count);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Server_secrets_stay_out_of_model_messages_and_denied_users_do_not_get_manifests(bool denied)
    {
        using var handler = new FakeWebHandler(); var server = new Server();
        handler.StreamFactory = (body, index) => Stream(body, index, "[[final:" + Nonce(body) + "]]hello");
        var client = Client(handler, server, options: new() { Servers = [new() { Label = "calculator", Client = server,
            Headers = new Dictionary<string, string> { ["Authorization"] = "synthetic-private-header" }, Environment = new Dictionary<string, string?> { ["SECRET"] = "synthetic-private-env" } }],
            IsServerAllowed = (scope, label) => !denied });
        await client.SendAsync(Scope(), Request());
        Assert.DoesNotContain("synthetic-private", handler.Turns.Single().Body.ToJsonString());
        Assert.Equal(denied ? 0 : 1, server.Lists);
    }
}
