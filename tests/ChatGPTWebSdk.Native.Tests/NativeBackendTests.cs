using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OAuth;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Tests;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Responses;
using Native = ChatGPTWebSdk.Native;

namespace ChatGPTWebSdk.Native.Tests;

public sealed class NativeBackendTests
{
    private static JsonObject Completed() => new()
    {
        ["id"] = "resp_native", ["object"] = "response", ["created_at"] = 1, ["status"] = "completed", ["model"] = "test-model", ["parallel_tool_calls"] = true,
        ["output"] = new JsonArray(new JsonObject { ["id"] = "msg_native", ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Hello", ["annotations"] = new JsonArray() }) })
    };
    private static string Events() => "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\nevent: response.completed\ndata: " +
        new JsonObject { ["type"] = "response.completed", ["response"] = Completed() }.ToJsonString() + "\n\n";
    private static OAuthSession Session() => new() { AccountId = "synthetic-account", AccessToken = "synthetic-native-oauth" };
    private static Native.ChatGPTWebRuntime Web(HttpClient http) => Native.ChatGPTWeb.Initialize(new Native.ChatGPTWebRuntimeOptions
    {
        Credentials = new StaticWebCredentialProvider("default", new() { AccessToken = "synthetic-native-web" }), HttpClient = http,
        Mode = Native.ChatGPTWebMode.ApiOnly, Endpoints = new(), IgnoreModelRestrictions = true, ConversationStore = new InMemoryConversationStore()
    });

    [Fact]
    public void Integration_references_the_real_strong_named_official_assembly()
    {
        Assert.NotEmpty(typeof(OpenAIClient).Assembly.GetName().GetPublicKeyToken()!);
        Assert.Equal(new Version(2,14,0,0), typeof(OpenAIClient).Assembly.GetName().Version);
        Assert.NotEqual(typeof(Native.ChatGPTWeb).Assembly, typeof(OpenAIClient).Assembly);
    }

    [Fact]
    public void Every_original_public_signature_resolves_with_the_selected_dependencies()
    {
        // Typed Chat/Responses calls alone do not load older streaming signatures on other feature clients.
        var methods = 0;
        foreach (var type in typeof(OpenAIClient).Assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods()) { _ = method.ReturnType; _ = method.GetParameters(); methods++; }
            foreach (var constructor in type.GetConstructors()) _ = constructor.GetParameters();
        }
        Assert.True(methods > 1000);
        Assert.Equal(new Version(1,15,0,0), typeof(ClientRetryPolicy).Assembly.GetName().Version);
    }

    [Theory]
    [InlineData("official", false, false)] [InlineData("official", false, true)]
    [InlineData("official", true, false)] [InlineData("official", true, true)]
    [InlineData("codex", false, false)] [InlineData("codex", false, true)]
    [InlineData("codex", true, false)] [InlineData("codex", true, true)]
    [InlineData("web", false, false)] [InlineData("web", false, true)]
    [InlineData("web", true, false)] [InlineData("web", true, true)]
    public async Task Original_clients_consume_each_backend_with_and_without_SSE(string backend, bool chat, bool streaming)
    {
        var calls = 0;
        using var webHandler = new FakeWebHandler(); using var webHttp = new HttpClient(webHandler, false);
        await using var web = Web(webHttp);
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(backend == "codex" ? "synthetic-native-oauth" : "synthetic-platform-key", request.Headers.Authorization!.Parameter);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal(backend == "codex" ? "chatgpt.com" : "api.openai.com", request.RequestUri!.Host);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("test-model", body["model"]!.GetValue<string>());
            if (backend == "codex" || !chat && streaming) return new(HttpStatusCode.OK) { Content = new StringContent(Events(), Encoding.UTF8, "text/event-stream") };
            if (!chat) { Assert.Equal("Hello", body["input"]![0]!["content"]![0]!["text"]!.GetValue<string>()); return FakeWebHandler.Json(Completed()); }
            var completion = new JsonObject { ["id"] = "chatcmpl_native", ["object"] = streaming ? "chat.completion.chunk" : "chat.completion", ["created"] = 1, ["model"] = "test-model",
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = "stop", [streaming ? "delta" : "message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Hello" } }) };
            return streaming ? new(HttpStatusCode.OK) { Content = new StringContent("data: " + completion.ToJsonString() + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") }
                : FakeWebHandler.Json(completion);
        });
        using var http = new HttpClient(handler, false);
        using var codex = new Native.ChatGPTCodex(new StaticOAuthSessionProvider(Session()), new() { DiscoverModelCapabilities = false }, http);
        OpenAIClient client = backend switch
        {
            "codex" => codex.CreateClient("thread"), "web" => web.CreateClient(threadId: "thread"),
            _ => Native.ChatGPTOfficial.CreateClient("synthetic-platform-key", new() { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) })
        };
        var text = "";
        if (chat)
        {
            var chats = client.GetChatClient("test-model");
            if (streaming) await foreach (var update in chats.CompleteChatStreamingAsync("Hello")) text += string.Concat(update.ContentUpdate.Select(p => p.Text));
            else text = (await chats.CompleteChatAsync("Hello")).Value.Content[0].Text;
        }
        else
        {
            var responses = client.GetResponsesClient();
            if (streaming) { await foreach (var update in responses.CreateResponseStreamingAsync("test-model", "Hello")) if (update is StreamingResponseOutputTextDeltaUpdate delta) text += delta.Delta; }
            else text = (await responses.CreateResponseAsync("test-model", "Hello")).Value.GetOutputText();
        }
        Assert.Equal(backend == "web" ? "Hello world" : "Hello", text);
        Assert.Equal(backend == "web" ? 0 : 1, calls); Assert.Equal(backend == "web" ? 1 : 0, webHandler.Turns.Count);
    }

    [Fact]
    public async Task Native_web_continuation_appends_to_a_real_binding_without_resending_history()
    {
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler, false); await using var runtime = Web(http);
        var client = runtime.CreateClient(threadId:"alice-chat").GetChatClient("test-model");
        await client.CompleteChatAsync("one"); await client.CompleteChatAsync("two");
        Assert.Equal(2, handler.Turns.Count);
        var second = handler.Turns[1].Body; Assert.Equal("conversation-1", second["conversation_id"]!.GetValue<string>());
        Assert.Equal("assistant-1", second["parent_message_id"]!.GetValue<string>()); Assert.Single(second["messages"]!.AsArray());
        Assert.Equal("two", second["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Official_embeddings_keep_platform_features_that_web_and_Codex_do_not_map()
    {
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            Assert.Equal("native-project", request.Headers.GetValues("OpenAI-Project").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!; Assert.Equal("embedding-model", body["model"]!.GetValue<string>());
            return FakeWebHandler.Json(new JsonObject { ["object"] = "list", ["model"] = "embedding-model", ["usage"] = new JsonObject { ["prompt_tokens"] = 1, ["total_tokens"] = 1 },
                ["data"] = new JsonArray(new JsonObject { ["object"] = "embedding", ["index"] = 0, ["embedding"] = Convert.ToBase64String(BitConverter.GetBytes(0.25f)) }) });
        });
        using var http = new HttpClient(handler);
        var client = Native.ChatGPTOfficial.CreateClient(new ApiKeyCredential("synthetic-api-key"), new() { Transport = new HttpClientPipelineTransport(http), ProjectId = "native-project" });
        var embedding = (await client.GetEmbeddingClient("embedding-model").GenerateEmbeddingAsync("text")).Value;
        Assert.Equal(0.25f, embedding.ToFloats().Span[0]);
    }

    [Fact]
    public void Official_factory_exposes_explicit_native_options_without_initializing_web()
    {
        var options = Native.ChatGPTOfficial.CreateClientOptions();
        Assert.Same(HttpClientPipelineTransport.Shared, options.Transport); Assert.Null(options.RetryPolicy);
        Assert.NotNull(Native.ChatGPTOfficial.CreateClient("synthetic-api-key"));
        Assert.Throws<ArgumentException>(() => Native.ChatGPTOfficial.CreateClient(" "));
    }

    [Fact]
    public async Task Disposing_native_runtimes_preserves_application_owned_HTTP_clients()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(FakeWebHandler.Json(new JsonObject()))); using var http = new HttpClient(handler);
        var web = Web(http); await web.DisposeAsync(); Assert.Throws<ObjectDisposedException>(() => web.CreateClient());
        var codex = new Native.ChatGPTCodex(new StaticOAuthSessionProvider(Session()), upstream:http); codex.Dispose(); Assert.Throws<ObjectDisposedException>(() => codex.CreateClient());
        using var result = await http.GetAsync("https://synthetic.local/"); Assert.True(result.IsSuccessStatusCode);
    }
}
