using System.Diagnostics;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Responses;

namespace ChatGPTWebSdk.Tests;

public sealed class McpScopeTransportTests
{
    private static string PidReply(JsonObject body, int index)
    {
        var text = body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>();
        var nonce = McpBridgeTests.Nonce(body);
        if (text.StartsWith("MCP tool results", StringComparison.Ordinal))
        {
            var json = text.Split('\n')[1];
            var pid = JsonNode.Parse(json)![0]!["result"]!["structuredContent"]!["pid"]!.GetValue<int>();
            return McpBridgeTests.Stream(body, index, "[[final:" + nonce + "]]" + pid);
        }
        return McpBridgeTests.Stream(body, index, McpBridgeTests.ToolReply(nonce,
            "[{\"id\":\"pid-" + index + "\",\"server\":\"identity\",\"name\":\"get_pid\",\"arguments\":{}}]"));
    }
    private static McpServerConfiguration Http(Uri endpoint, string token, McpWebTransport transport = McpWebTransport.Auto) => new()
    { Label = "identity", Endpoint = endpoint, Transport = transport, Headers = new Dictionary<string, string> { ["Authorization"] = token }, AllowedTools = ["get_pid"] };
    public static IEnumerable<object[]> Connections()
    {
        foreach (var shared in new[] { false, true })
        foreach (var streaming in new[] { false, true })
        foreach (var context in new[] { "normal", "temporary", "project" }) yield return [shared, streaming, context];
    }
    [Theory, MemberData(nameof(Connections))]
    public async Task Scoped_HTTP_servers_use_independent_endpoints_headers_and_transports_or_reuse_the_same_endpoint(bool shared, bool streaming, string context)
    {
        await using var initial = await McpTransportTests.HttpFixture.StartAsync("http-sse", "Bearer synthetic-initial");
        await using var chat = shared ? null : await McpTransportTests.HttpFixture.StartAsync("http-json", "Bearer synthetic-chat");
        await using var message = shared ? null : await McpTransportTests.HttpFixture.StartAsync("legacy", "Bearer synthetic-message");
        var chatConfig = Http(chat?.Endpoint ?? initial.Endpoint, shared ? "Bearer synthetic-initial" : "Bearer synthetic-chat", McpWebTransport.StreamableHttp);
        var messageConfig = Http(message?.Endpoint ?? initial.Endpoint, shared ? "Bearer synthetic-initial" : "Bearer synthetic-message", shared ? McpWebTransport.Auto : McpWebTransport.Sse);
        using var handler = new FakeWebHandler { StreamFactory = PidReply };
        using var runtime = new ChatGPTWebRuntime(new()
        {
            Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic" }), AccountId = "account", UserId = "alice",
            HttpClient = new(handler, false), ConversationStore = new InMemoryConversationStore(), Endpoints = new(), TemporaryChat = context == "temporary", ProjectId = context == "project" ? "g-p-project" : null,
            Mcp = new() { Servers = [Http(initial.Endpoint, "Bearer synthetic-initial")] }
        });
        var responses = runtime.CreateClient(threadId: "research").GetResponsesClient();
        async Task<int> Call(McpServerConfiguration config)
        {
            var options = new CreateResponseOptions { Model = "fixture-model", StreamingEnabled = streaming };
            options.InputItems.Add(ResponseItem.CreateUserMessageItem("Use identity.get_pid to identify the connected server"));
            options.Tools.Add(ResponseTool.CreateMcpTool("identity", config.Endpoint!, toolCallApprovalPolicy: DefaultMcpToolCallApprovalPolicy.NeverRequireApproval));
            if (!streaming) return int.Parse((await responses.CreateResponseAsync(options)).Value.GetOutputText(), System.Globalization.CultureInfo.InvariantCulture);
            var answer = "";
            await foreach (var update in responses.CreateResponseStreamingAsync(options)) if (update is StreamingResponseOutputTextDeltaUpdate delta) answer += delta.Delta;
            return int.Parse(answer, System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.Equal(initial.Process.Id, await Call(Http(initial.Endpoint, "Bearer synthetic-initial")));
        runtime.SetChatMcp(new() { Servers = [chatConfig] }, "research");
        Assert.Equal((chat ?? initial).Process.Id, await Call(chatConfig));
        using (runtime.UseMessageMcp(new() { Servers = [messageConfig] })) Assert.Equal((message ?? initial).Process.Id, await Call(messageConfig));
        Assert.Equal((chat ?? initial).Process.Id, await Call(chatConfig));
        runtime.SetChatMcp(null, "research"); Assert.Equal(initial.Process.Id, await Call(Http(initial.Endpoint, "Bearer synthetic-initial")));
        Assert.Equal(10, handler.Turns.Count);
        Assert.All(handler.Turns, t =>
        {
            Assert.DoesNotContain("synthetic-initial", t.Body.ToJsonString()); Assert.DoesNotContain("synthetic-chat", t.Body.ToJsonString()); Assert.DoesNotContain("synthetic-message", t.Body.ToJsonString());
            Assert.DoesNotContain(initial.Endpoint.ToString(), t.Body.ToJsonString());
            if (context == "temporary") Assert.True(t.Body["history_and_training_disabled"]!.GetValue<bool>());
            if (context == "project") Assert.Equal("g-p-project", t.Body["gizmo_id"]!.GetValue<string>());
        });
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Message_only_stdio_servers_close_the_owned_process_and_do_not_require_initialization_servers(bool temporary)
    {
        using var handler = new FakeWebHandler { StreamFactory = PidReply }; var client = handler.Client();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-server.mjs");
        var config = new McpServerConfiguration { Label = "identity", Command = "node", Arguments = [fixture], Transport = McpWebTransport.Stdio, AllowedTools = ["get_pid"],
            Environment = new Dictionary<string, string?> { ["WEBSDK_SYNTHETIC_SCOPE"] = "message" }, WorkingDirectory = AppContext.BaseDirectory };
        var result = await client.SendAsync(new("account", "alice"), new WebTurnRequest { Model = "fixture-model", Messages = [WebInputMessage.User("Use identity.get_pid")], TemporaryChat = temporary, Mcp = new() { Servers = [config] } });
        var pid = int.Parse(result.Text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.Null(client.GetChatMcp(new("account", "alice"))); Assert.DoesNotContain("WEBSDK_SYNTHETIC_SCOPE", handler.Turns[0].Body.ToJsonString());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Responses_URL_checks_use_the_active_scope_and_do_not_register_arbitrary_request_endpoints(bool messageOnly)
    {
        using var handler = new FakeWebHandler(); var client = handler.Client(); var scope = new ConversationScope("account", "alice");
        var config = new McpServerConfiguration { Label = "identity", Endpoint = new("https://scoped.example.test/mcp") };
        var scoped = new McpScopeOptions { Servers = [config] };
        if (!messageOnly) client.SetChatMcp(scope, scoped);
        var adapter = messageOnly ? new OpenAiWebAdapter(client).WithMcp(scoped) : new OpenAiWebAdapter(client);
        var body = new JsonObject { ["model"] = "fixture-model", ["input"] = "Use tools", ["tools"] = new JsonArray(new JsonObject { ["type"] = "mcp", ["server_label"] = "identity", ["server_url"] = config.Endpoint!.ToString() }) };
        await adapter.PrepareResponseAsync(scope, body);
        body["tools"]![0]!["server_url"] = "https://another.example.test/mcp";
        Assert.Equal("mcp_server_denied", (await Assert.ThrowsAsync<SdkException>(() => adapter.PrepareResponseAsync(scope, body))).Code);
        body["tools"]![0]!["server_label"] = "unregistered";
        Assert.Equal("mcp_server_not_registered", (await Assert.ThrowsAsync<SdkException>(() => adapter.PrepareResponseAsync(scope, body))).Code); Assert.Empty(handler.Turns);
    }
}
