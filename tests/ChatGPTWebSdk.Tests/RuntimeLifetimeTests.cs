using System.Text.Json.Nodes;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;

namespace ChatGPTWebSdk.Tests;

public sealed class RuntimeLifetimeTests
{
    private sealed class ExternalProvider : IWebSentinelSessionProvider, IAsyncDisposable
    {
        public int Closed;
        public ValueTask DisposeAsync() { Closed++; return ValueTask.CompletedTask; }
        public ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default) =>
            ValueTask.FromResult(new WebAuthorizedSession(credentials, new() { Token = "synthetic", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) }));
    }

    [Fact]
    public async Task Runtime_warmup_in_API_only_mode_needs_neither_a_browser_nor_an_HTTP_request()
    {
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler, false);
        await using var runtime = new ChatGPTWebRuntime(new() { Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("default", new()), HttpClient = http, ConversationStore = new InMemoryConversationStore() });
        await runtime.WarmUpAsync(); Assert.Empty(handler.Turns);
    }

    [Fact]
    public async Task Runtime_disposal_leaves_external_authentication_providers_owned_by_the_application()
    {
        var provider = new ExternalProvider();
        var runtime = new ChatGPTWebRuntime(new() { Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("default", new()), SentinelSessionProvider = provider, ConversationStore = new InMemoryConversationStore() });
        await runtime.DisposeAsync(); runtime.Dispose(); Assert.Equal(0, provider.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.WarmUpAsync());
    }

    [Fact]
    public async Task Runtime_disposal_closes_its_reused_MCP_connections_and_rejects_future_sends()
    {
        await using var fixture = await McpTransportTests.HttpFixture.StartAsync("http-json");
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler, false);
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, "[[final:" + McpBridgeTests.Nonce(body) + "]]Ready");
        var runtime = new ChatGPTWebRuntime(new()
        {
            Mode = ChatGPTWebMode.ApiOnly, Credentials = new StaticWebCredentialProvider("default", new() { AccessToken = "synthetic" }), HttpClient = http, Endpoints = new(), ConversationStore = new InMemoryConversationStore(),
            Mcp = new() { Servers = [new() { Label = "tools", Endpoint = fixture.Endpoint }] }
        });
        var scope = runtime.ResolveScope(runtime.ClientKey);
        await runtime.Web.SendAsync(scope, "Hello", "gpt-6"); await runtime.DisposeAsync();
        using var statsHttp = new HttpClient(); var stats = JsonNode.Parse(await statsHttp.GetStringAsync(new Uri(fixture.Endpoint, "/_stats")))!;
        Assert.Equal(1, stats["deletes"]!.GetValue<int>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.Web.SendAsync(scope, "Next", "gpt-6"));
    }
}
