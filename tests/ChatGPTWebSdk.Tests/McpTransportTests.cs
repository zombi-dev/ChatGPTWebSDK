using System.Diagnostics;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Responses;

namespace ChatGPTWebSdk.Tests;

public sealed class McpTransportTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-server.mjs");
    internal sealed class HttpFixture : IAsyncDisposable
    {
        public required Process Process { get; init; }
        public required Uri Endpoint { get; init; }
        public static async Task<HttpFixture> StartAsync(string mode)
        {
            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Fixture); start.ArgumentList.Add(mode);
            var process = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var hello = JsonNode.Parse((await process.StandardOutput.ReadLineAsync(timeout.Token))!)!;
                return new() { Process = process, Endpoint = new("http://127.0.0.1:" + hello["port"]!.GetValue<int>() + (mode == "legacy" ? "/sse" : "/mcp")) };
            }
            catch { process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync(); Process.Dispose();
        }
    }
    [Theory, InlineData("http-json", McpWebTransport.StreamableHttp), InlineData("http-sse", McpWebTransport.StreamableHttp), InlineData("http-json", McpWebTransport.Auto), InlineData("legacy", McpWebTransport.Sse), InlineData("legacy", McpWebTransport.Auto)]
    public async Task Real_HTTP_and_legacy_SSE_connections_initialize_page_tools_and_call_them(string mode, McpWebTransport transport)
    {
        await using var fixture = await HttpFixture.StartAsync(mode);
        await using var client = await McpWebClient.ConnectAsync(new() { Label = "calculator", Endpoint = fixture.Endpoint, Transport = transport });
        var tools = await client.ListToolsAsync();
        Assert.Equal(3, tools.Count); Assert.All(tools, t => Assert.True(t.ReadOnly));
        var result = await client.CallToolAsync("add", new() { ["a"] = 7, ["b"] = 8 });
        Assert.Equal(15, result["structuredContent"]!["sum"]!.GetValue<int>()); Assert.Equal("15", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Real_stdio_connection_preserves_structured_results_and_closes_only_its_owned_process()
    {
        int pid;
        await using (var client = await McpWebClient.ConnectAsync(new() { Label = "local", Command = "node", Arguments = [Fixture], Transport = McpWebTransport.Stdio }))
        {
            Assert.Equal(3, (await client.ListToolsAsync()).Count);
            var value = JsonNode.Parse("{\"nested\":[true,null,\"日本語 😀\"],\"number\":5}")!;
            var result = await client.CallToolAsync("echo", new() { ["value"] = value.DeepClone() });
            Assert.True(JsonNode.DeepEquals(value, result["structuredContent"]!["value"]));
            pid = (await client.CallToolAsync("get_pid", new()))["structuredContent"]!["pid"]!.GetValue<int>();
        }
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task Real_stdio_tools_are_used_by_the_conversation_bridge_and_stop_after_final_generation()
    {
        using var handler = new FakeWebHandler();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Command = "node", Arguments = [Fixture], AllowedTools = ["add"] }] });
        Assert.Equal("5", (await client.SendAsync(new("account", "alice"), "Add 2 and 3", "fixture-model")).Text);
        Assert.Contains("\"sum\":5", handler.Turns[1].Body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Official_MCP_tool_declarations_select_a_registered_remote_server_and_work_with_streaming(bool streaming)
    {
        await using var fixture = await HttpFixture.StartAsync("http-json");
        using var handler = new FakeWebHandler();
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        using var runtime = new ChatGPTWebRuntime(new() { Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic" }),
            AccountId = "account", UserId = "alice", HttpClient = new(handler, false), ConversationStore = new InMemoryConversationStore(), Endpoints = new(),
            Mcp = new() { Servers = [new() { Label = "calculator", Endpoint = fixture.Endpoint }] } });
        var options = new CreateResponseOptions { Model = "fixture-model", StreamingEnabled = streaming };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem("Add 2 and 3"));
        options.Tools.Add(ResponseTool.CreateMcpTool("calculator", fixture.Endpoint, toolCallApprovalPolicy: DefaultMcpToolCallApprovalPolicy.NeverRequireApproval));
        var responses = runtime.CreateClient().GetResponsesClient();
        var text = "";
        if (streaming) { await foreach (var item in responses.CreateResponseStreamingAsync(options)) if (item is StreamingResponseOutputTextDeltaUpdate delta) text += delta.Delta; }
        else text = (await responses.CreateResponseAsync(options)).Value.GetOutputText();
        Assert.Equal("5", text); Assert.Equal(2, handler.Turns.Count);
    }

    [Theory, InlineData("label"), InlineData("sources"), InlineData("url"), InlineData("fragment"), InlineData("userinfo"), InlineData("transport"), InlineData("header"), InlineData("command"), InlineData("duplicates"), InlineData("limits")]
    public void Invalid_server_configurations_fail_before_starting_any_connection(string invalid)
    {
        var server = new McpServerConfiguration { Label = "valid", Endpoint = new("http://127.0.0.1:1/mcp") };
        server = invalid switch
        {
            "label" => server with { Label = "bad\nlabel" }, "sources" => server with { Command = "node" },
            "url" => server with { Endpoint = new("file:///private") }, "fragment" => server with { Endpoint = new("https://example.com/mcp#fragment") },
            "userinfo" => server with { Endpoint = new("https://user:secret@example.com/mcp") }, "transport" => server with { Transport = McpWebTransport.Stdio },
            "header" => server with { Headers = new Dictionary<string, string> { ["Authorization"] = "bad\r\nHeader" } },
            "command" => server with { Endpoint = null, Command = "" }, _ => server
        };
        using var handler = new FakeWebHandler();
        Assert.Throws<ArgumentException>(() => new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = invalid == "duplicates" ? [server, server] : [server], MaxToolCalls = invalid == "limits" ? 0 : 32 }));
        Assert.Empty(handler.Turns);
    }
}
