using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OpenAI;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace ChatGPTWebSdk.Tests;

public sealed class ProxyTests
{
    private const string AliceKey = "alice-fixture-key-000000000000";
    private const string BobKey = "bob-fixture-key-000000000000";

    private sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeWebHandler Handler { get; } = new();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "websdk-proxy-" + Guid.NewGuid());
        private readonly string? _previousConfig = Environment.GetEnvironmentVariable("CHATGPT_WEB_CONFIG");
        public Factory()
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "config.session.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                sessionDirectory = Path.Combine(_directory, "sessions"),
                accounts = new[] { new { id = "account", credentials = new { accessToken = "fixture-token" } } },
                clients = new[] { new { apiKey = AliceKey, accountId = "account", userId = "alice" }, new { apiKey = BobKey, accountId = "account", userId = "bob" } }
            }));
            Environment.SetEnvironmentVariable("CHATGPT_WEB_CONFIG", path);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ChatGptWebTransport>();
                services.RemoveAll<IConversationStore>();
                services.AddSingleton(Handler.Client().Transport);
                services.AddSingleton<IConversationStore, InMemoryConversationStore>();
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Handler.Dispose();
                Environment.SetEnvironmentVariable("CHATGPT_WEB_CONFIG", _previousConfig);
                TestDirectory.Delete(_directory);
            }
        }
    }

    private static HttpClient Authorized(Factory factory, string key)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }
    [Fact]
    public async Task Kestrel_loopback_server_accepts_real_TCP_requests_and_preserves_turns()
    {
        using var factory = new Factory();
        factory.UseKestrel(0);
        using var http = Authorized(factory, AliceKey);
        Assert.NotEqual(80, http.BaseAddress!.Port);
        var capabilities = await http.GetFromJsonAsync<JsonObject>("/capabilities");
        Assert.Equal(353, capabilities!["spec"]!["operationCount"]!.GetValue<int>());
        var first = await http.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "one" });
        first.EnsureSuccessStatusCode();
        var second = await http.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "two" });
        second.EnsureSuccessStatusCode();
        Assert.Equal("assistant-1", factory.Handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal("conversation-1", factory.Handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        var binding = await http.GetFromJsonAsync<JsonObject>("/web/binding");
        Assert.Equal("conversation-1", binding!["conversationId"]!.GetValue<string>());
    }
    [Fact]
    public async Task Application_client_cannot_attach_an_arbitrary_account_conversation()
    {
        using var factory = new Factory();
        using var alice = Authorized(factory, AliceKey);
        var result = await alice.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "hello", conversation = "another-users-conversation" });
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/web/link", new { conversation_id = "external" })).StatusCode);
        Assert.Empty(factory.Handler.Turns);
    }
    [Fact]
    public async Task Actual_HTTP_proxy_supports_appends_retrieval_user_isolation_and_thread_selection()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();
        var sdk = new OpenAiClient(http, AliceKey, new("http://localhost/v1/"));
        var first = await sdk.Responses.CreateAsync(new() { Model = "gpt-6", Input = JsonValue.Create("one")! });
        var second = await sdk.Responses.CreateAsync(new() { Model = "gpt-6", Input = JsonValue.Create("two")!, PreviousResponseId = first["id"]!.GetValue<string>() });
        Assert.Equal(first["conversation"]!["id"]!.GetValue<string>(), second["conversation"]!["id"]!.GetValue<string>());
        Assert.Equal("assistant-1", factory.Handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        var retrieved = await sdk.Responses.GetAsync(first["id"]!.GetValue<string>());
        Assert.Equal("Hello world", retrieved["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        using var bob = Authorized(factory, BobKey);
        bob.DefaultRequestHeaders.Add("X-ChatGPT-User-Id", "alice");
        var denied = await bob.GetAsync("/v1/responses/" + first["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        http.DefaultRequestHeaders.Add("X-ChatGPT-Thread-Id", "other-thread");
        await sdk.Responses.CreateAsync(new() { Model = "gpt-6", Input = JsonValue.Create("independent")! });
        Assert.Null(factory.Handler.Turns[2].Body["conversation_id"]);
        using var models = await sdk.Operations.ListModelsAsync();
        Assert.Equal("gpt-6", (await models.ReadJsonAsync())!["data"]![0]!["id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Actual_HTTP_proxy_stream_is_readable_by_the_official_operation_client()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();
        var sdk = new OpenAiClient(http, AliceKey, new("http://localhost/v1/"));
        var events = new List<JsonNode>();
        await foreach (var item in sdk.Responses.StreamAsync(new() { Model = "gpt-6", Input = JsonValue.Create("stream me")! }))
            events.Add(JsonNode.Parse(item.Data)!);
        Assert.All(events, e => Assert.True(e["type"] is not null, e.ToJsonString()));
        Assert.Equal("response.created", events[0]["type"]!.GetValue<string>());
        Assert.Equal("response.completed", events[^1]["type"]!.GetValue<string>());
        var id = events[^1]["response"]!["id"]!.GetValue<string>();
        Assert.Equal(id, (await sdk.Responses.GetAsync(id))["id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Actual_HTTP_proxy_rejects_bad_keys_and_unmapped_features_without_upstream_calls()
    {
        using var factory = new Factory();
        using var noAuth = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("/web/binding")).StatusCode);
        using var alice = Authorized(factory, AliceKey);
        var unsupported = await alice.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "hello", temperature = 0.1 });
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await alice.PostAsJsonAsync("/v1/embeddings", new { model = "fixture", input = "hello" })).StatusCode);
        Assert.Empty(factory.Handler.Turns);
        var capabilities = await alice.GetFromJsonAsync<JsonObject>("/capabilities");
        Assert.False(capabilities!["liveVerified"]!.GetValue<bool>());
        Assert.Equal(353, capabilities["spec"]!["operationCount"]!.GetValue<int>());
    }
    [Fact]
    public async Task Actual_HTTP_proxy_preserves_challenge_HTTP_status_before_stream_starts()
    {
        using var factory = new Factory();
        factory.Handler.Requirements = new() { ["turnstile"] = new JsonObject { ["required"] = true } };
        using var http = Authorized(factory, AliceKey);
        var result = await http.PostAsJsonAsync("/v1/responses", new { model = "gpt-6", input = "hello", stream = true });
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Empty(factory.Handler.Turns);
    }
}

