using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatGPTWebSdk.Tests;

public sealed class McpProxyTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeWebHandler Handler { get; } = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "websdk-mcp-proxy-" + Guid.NewGuid());
        private readonly string? _previous = Environment.GetEnvironmentVariable("CHATGPT_WEB_CONFIG");
        public Factory(Uri endpoint)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                sessionDirectory = Path.Combine(_directory, "sessions"), mode = "apiOnly", httpDriver = "dotNet",
                accounts = new[] { new { id = "account", credentials = new { accessToken = "synthetic-proxy-token" } } },
                clients = new[] { new { apiKey = "alice-synthetic-key-0000", accountId = "account", userId = "alice", mcpServers = new[] { "calculator" } },
                    new { apiKey = "bob-synthetic-key-0000", accountId = "account", userId = "bob", mcpServers = Array.Empty<string>() } },
                mcp = new { servers = new[] { new { label = "calculator", endpoint = endpoint.ToString(), transport = "streamableHttp", allowedTools = new[] { "add" } } } }
            }));
            Environment.SetEnvironmentVariable("CHATGPT_WEB_CONFIG", path);
            Handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index == 1 ? McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) : "[[final:" + McpBridgeTests.Nonce(body) + "]]5");
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ChatGptWebTransport>(); services.RemoveAll<IConversationStore>();
            services.AddSingleton(Handler.Client().Transport); services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        });
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { Environment.SetEnvironmentVariable("CHATGPT_WEB_CONFIG", _previous); Handler.Dispose(); TestDirectory.Delete(_directory); }
        }
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Proxy_chat_requests_complete_the_MCP_loop_and_stream_only_the_answer(bool streaming)
    {
        await using var server = await McpTransportTests.HttpFixture.StartAsync("http-json");
        using var factory = new Factory(server.Endpoint); using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "alice-synthetic-key-0000");
        using var response = await http.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-6", messages = new[] { new { role = "user", content = "Add 2 and 3" } }, stream = streaming });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("[[mcp:", text); Assert.DoesNotContain("[[final:", text);
        if (streaming) { Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType); Assert.Contains("data: [DONE]", text); }
        else Assert.Equal("5", JsonNode.Parse(text)!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal(2, factory.Handler.Turns.Count);
    }
    [Fact]
    public async Task Proxy_MCP_request_cannot_override_the_client_server_permissions()
    {
        await using var server = await McpTransportTests.HttpFixture.StartAsync("http-json");
        using var factory = new Factory(server.Endpoint); using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "bob-synthetic-key-0000");
        using var response = await http.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "Use calculator", tools = new[] { new { type = "mcp", server_label = "calculator", server_url = server.Endpoint.ToString(), require_approval = "never" } } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Empty(factory.Handler.Turns);
        Assert.Equal("mcp_server_denied", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>());
    }
}
