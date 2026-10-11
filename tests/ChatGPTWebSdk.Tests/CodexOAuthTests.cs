using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OAuth;
using ChatGPTWebSdk.Protocol;
using OpenAI;
using OpenAI.Responses;
using Xunit;
using static ChatGPTWebSdk.Tests.OAuthAuthenticationTests;

namespace ChatGPTWebSdk.Tests;

public sealed class CodexOAuthTests
{
    private static JsonObject Completed(string id = "resp_fixture", JsonArray? output = null) => new()
    {
        ["id"] = id, ["object"] = "response", ["created_at"] = 1, ["status"] = "completed", ["model"] = "gpt-6", ["parallel_tool_calls"] = true,
        ["output"] = output ?? new JsonArray(new JsonObject { ["id"] = "msg_fixture", ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Hello", ["annotations"] = new JsonArray() }) }),
        ["usage"] = new JsonObject { ["input_tokens"] = 3, ["output_tokens"] = 1, ["total_tokens"] = 4,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 } }
    };
    private static HttpResponseMessage Events(params JsonObject[] events) => new(HttpStatusCode.OK)
        { Content = new StringContent(string.Concat(events.Select(e => "event: " + e["type"]!.GetValue<string>() + "\ndata: " + e.ToJsonString() + "\n\n")), Encoding.UTF8, "text/event-stream") };
    private static JsonObject Terminal(JsonObject? response = null) => new() { ["type"] = "response.completed", ["response"] = response ?? Completed() };
    private static HttpRequestMessage Request(JsonObject body, string thread = "default", string path = "responses")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://sdk.local/v1/" + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-ChatGPT-Thread-Id", thread); request.Headers.Add("Cookie", "must-not-forward=1"); request.Headers.Authorization = new("Bearer", "application-key-must-not-forward"); return request;
    }
    private static HttpClient Client(Handler handler, CodexOAuthOptions? options = null) => new(new CodexOAuthHttpHandler(new StaticOAuthSessionProvider(Session()),
        options ?? new() { DiscoverModelCapabilities = false }, new HttpClient(handler, false)));

    [Fact]
    public async Task ResponsesNormalizeAndCollectOnlyTerminalSnapshots()
    {
        using var upstream = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.AbsoluteUri);
            Assert.StartsWith("synthetic.", request.Headers.Authorization!.Parameter); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("account", request.Headers.GetValues("chatgpt-account-id").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.True(body["stream"]!.GetValue<bool>()); Assert.False(body["store"]!.GetValue<bool>()); Assert.Equal("", body["instructions"]!.GetValue<string>());
            Assert.Null(body["max_output_tokens"]); Assert.Equal("hello", body["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.Contains(body["include"]!.AsArray(), n => n!.GetValue<string>() == "reasoning.encrypted_content");
            return Events(new JsonObject() { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = "partial", ["status"] = "in_progress" } }, Terminal());
        });
        using var client = Client(upstream); using var request = Request(new() { ["model"] = "gpt-6", ["input"] = "hello", ["store"] = true, ["max_output_tokens"] = 10 });
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        Assert.Equal("resp_fixture", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>()); Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task TerminalWithEmptyOutputRetainsCompletedItems()
    {
        var completed = Completed(); var output = completed["output"]![0]!.DeepClone(); completed["output"] = new JsonArray();
        using var upstream = new Handler((_, _) => Task.FromResult(Events(new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = output.DeepClone() }, Terminal(completed))));
        using var client = Client(upstream); using var request = Request(new() { ["model"] = "gpt-6", ["input"] = "hello" });
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        Assert.Equal("Hello", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["output"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("done")]
    [InlineData("error")]
    [InlineData("malformed")]
    public async Task IncompleteOrErrorStreamsNeverReturnCreatedAsCompletedOrRetry(string failure)
    {
        using var upstream = new Handler((_, _) => Task.FromResult(failure switch
        {
            "error" => Events(new JsonObject() { ["type"] = "error", ["message"] = "private-synthetic-secret" }),
            "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: not json\n\n", Encoding.UTF8, "text/event-stream") },
            "done" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") },
            _ => Events(new JsonObject() { ["type"] = "response.created", ["response"] = new JsonObject { ["id"] = "partial", ["status"] = "in_progress" } })
        }));
        using var client = Client(upstream); using var request = Request(new() { ["model"] = "gpt-6", ["input"] = "hello" });
        using var response = await client.SendAsync(request);
        Assert.False(response.IsSuccessStatusCode); Assert.DoesNotContain("private-synthetic-secret", await response.Content.ReadAsStringAsync()); Assert.Equal(1, upstream.Calls);
    }

    [Theory]
    [InlineData("/v1/conversations")]
    [InlineData("/v1/files")]
    [InlineData("/v1/embeddings")]
    [InlineData("/v1/responses/resp_fixture")]
    [InlineData("/v1/audio/speech")]
    public async Task UnsupportedOperationsFailBeforeCredentialsOrNetwork(string path)
    {
        using var upstream = new Handler((_, _) => throw new Exception("Unsupported requests must not reach upstream.")); using var client = Client(upstream);
        using var response = await client.GetAsync("https://sdk.local" + path); Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode); Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData(301)] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task RejectedRequestsPreserveStatusAndNeverReplayOrExposeBodies(int status)
    {
        using var upstream = new Handler((_, _) => Task.FromResult(Json(new() { ["error"] = "private-synthetic-secret" }, (HttpStatusCode)status)));
        using var client = Client(upstream); using var request = Request(new() { ["model"] = "gpt-6", ["input"] = "hello" });
        using var response = await client.SendAsync(request); Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.DoesNotContain("private-synthetic-secret", await response.Content.ReadAsStringAsync()); Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task CachedContinuationReplaysHistoryButNeverCreatesAWebsiteConversation()
    {
        var inputs = new List<JsonArray>();
        using var upstream = new Handler(async (request, ct) =>
        { var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!; Assert.Null(body["previous_response_id"]); Assert.Null(body["conversation_id"]); inputs.Add((JsonArray)body["input"]!.DeepClone()); return Events(Terminal()); });
        using var client = Client(upstream);
        using (var first = Request(new() { ["model"] = "gpt-6", ["input"] = "one" })) using (var response = await client.SendAsync(first)) response.EnsureSuccessStatusCode();
        using (var second = Request(new() { ["model"] = "gpt-6", ["input"] = "two", ["previous_response_id"] = "resp_fixture" })) using (var response = await client.SendAsync(second)) response.EnsureSuccessStatusCode();
        Assert.Single(inputs[0]); Assert.Equal(3, inputs[1].Count);
        Assert.Equal("one", inputs[1][0]!["content"]![0]!["text"]!.GetValue<string>()); Assert.Equal("two", inputs[1][2]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CachedResponsesAndItemReferencesNeverCrossThreads(bool itemReference)
    {
        using var upstream = new Handler((_, _) => Task.FromResult(Events(Terminal()))); using var client = Client(upstream);
        using (var first = Request(new() { ["model"] = "gpt-6", ["input"] = "one" }, "alice")) using (var response = await client.SendAsync(first)) response.EnsureSuccessStatusCode();
        var body = new JsonObject { ["model"] = "gpt-6", ["input"] = itemReference ? new JsonArray(new JsonObject { ["type"] = "item_reference", ["id"] = "msg_fixture" }) : JsonValue.Create("two") };
        if (!itemReference) body["previous_response_id"] = "resp_fixture";
        using var other = Request(body, "bob"); using var rejected = await client.SendAsync(other);
        Assert.False(rejected.IsSuccessStatusCode); Assert.Contains("codex_history_missing", await rejected.Content.ReadAsStringAsync()); Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task AccountAwareDiscoveryUsesLiteCapabilitiesAndRetainsExplicitReasoning()
    {
        using var upstream = new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("client_version=0.144.1", request.RequestUri!.Query);
                return Json(new() { ["models"] = new JsonArray(new JsonObject { ["slug"] = "gpt-6", ["use_responses_lite"] = true, ["supported_in_api"] = true, ["default_reasoning_level"] = "high", ["support_verbosity"] = true, ["default_verbosity"] = "low" },
                    new JsonObject { ["slug"] = "hidden", ["visibility"] = "hide" }, new JsonObject { ["slug"] = "no-api", ["supported_in_api"] = false }) });
            }
            Assert.Equal("true", request.Headers.GetValues("x-openai-internal-codex-responses-lite").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("minimal", body["reasoning"]!["effort"]!.GetValue<string>()); Assert.Equal("all_turns", body["reasoning"]!["context"]!.GetValue<string>());
            Assert.Equal("low", body["text"]!["verbosity"]!.GetValue<string>()); Assert.Equal("", body["instructions"]!.GetValue<string>());
            Assert.Null(body["tools"]); Assert.Equal("additional_tools", body["input"]![0]!["type"]!.GetValue<string>()); Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());
            return Events(Terminal());
        });
        using var client = Client(upstream, new());
        using (var models = await client.GetAsync("https://sdk.local/v1/models")) Assert.Single(JsonNode.Parse(await models.Content.ReadAsStringAsync())!["data"]!.AsArray());
        using var generation = Request(new() { ["model"] = "gpt-6", ["input"] = "hello", ["instructions"] = "be brief", ["reasoning"] = new JsonObject { ["effort"] = "minimal" },
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "lookup" }) });
        using var result = await client.SendAsync(generation); result.EnsureSuccessStatusCode(); Assert.Equal(2, upstream.Calls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OfficialClientsConsumeResponsesAndChatWithAndWithoutSse(bool chat, bool streaming)
    {
        using var upstream = new Handler((_, _) => Task.FromResult(Events(new JsonObject() { ["type"] = "response.output_text.delta", ["delta"] = "Hello" }, Terminal())));
        using var http = new HttpClient(upstream, false); using var runtime = new ChatGPTCodex(new StaticOAuthSessionProvider(Session()), new() { DiscoverModelCapabilities = false }, http);
        var client = runtime.CreateClient("thread"); var text = "";
        if (chat)
        {
            var chats = client.GetChatClient("gpt-6");
            if (streaming) await foreach (var update in chats.CompleteChatStreamingAsync("hello")) text += string.Concat(update.ContentUpdate.Select(p => p.Text));
            else text = (await chats.CompleteChatAsync("hello")).Value.Content[0].Text;
        }
        else
        {
            var responses = client.GetResponsesClient();
            if (streaming) { await foreach (var update in responses.CreateResponseStreamingAsync("gpt-6", "hello")) if (update is StreamingResponseOutputTextDeltaUpdate delta) text += delta.Delta; }
            if (!streaming) text = (await responses.CreateResponseAsync("gpt-6", "hello")).Value.GetOutputText();
        }
        Assert.Equal("Hello", text); Assert.Equal(1, upstream.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChatToolArgumentsAreEmittedOnceEvenWithoutDeltas(bool deltas)
    {
        var call = new JsonObject { ["id"] = "fc_item", ["type"] = "function_call", ["call_id"] = "call_fixture", ["name"] = "lookup", ["arguments"] = "{\"key\":1}" };
        var events = new List<JsonObject> { new() { ["type"] = "response.output_item.added", ["item"] = call.DeepClone() } };
        if (deltas) events.Add(new() { ["type"] = "response.function_call_arguments.delta", ["item_id"] = "fc_item", ["delta"] = "{\"key\":1}" });
        events.Add(new() { ["type"] = "response.output_item.done", ["item"] = call.DeepClone() }); events.Add(Terminal(Completed(output: new JsonArray(call))));
        using var upstream = new Handler((_, _) => Task.FromResult(Events(events.ToArray()))); using var client = Client(upstream);
        using var request = Request(new() { ["model"] = "gpt-6", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }), ["stream"] = true }, path:"chat/completions");
        using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        var frames = new List<JsonNode>(); await foreach (var item in ServerSentEvents.ReadAsync(await response.Content.ReadAsStreamAsync())) if (item.Data != "[DONE]") frames.Add(JsonNode.Parse(item.Data)!);
        var arguments = string.Concat(frames.Select(n => n["choices"]?[0]?["delta"]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>()));
        Assert.Equal("{\"key\":1}", arguments); Assert.Contains(frames, n => n["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "tool_calls");
    }

    [Fact]
    public async Task DisposingChatBeforeReadingUpstreamClosesItsResponse()
    {
        var content = new TrackedContent("data: {}\n\n");
        using var upstream = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })); using var client = Client(upstream);
        using var request = Request(new() { ["model"] = "gpt-6", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }), ["stream"] = true }, path:"chat/completions");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.False(content.Closed); response.Dispose(); Assert.True(content.Closed);
    }

    private sealed class TrackedContent(string value) : StringContent(value, Encoding.UTF8, "text/event-stream")
    {
        internal bool Closed;
        protected override void Dispose(bool disposing) { if (disposing) Closed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task ReadCancellationStopsAStalledStreamAndDisposesUpstream()
    {
        var prefix = Encoding.UTF8.GetBytes("data: {\"type\":\"response.created\",\"response\":{\"id\":\"pending\"}}\n\n");
        var stream = new BlockingStream(prefix);
        using var upstream = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })); using var client = Client(upstream);
        using var request = Request(new() { ["model"] = "gpt-6", ["input"] = "hello", ["stream"] = true });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); var result = await response.Content.ReadAsStreamAsync();
        Assert.True(await result.ReadAsync(new byte[8192]) > 0);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.CopyToAsync(Stream.Null, cancelled.Token));
        response.Dispose(); Assert.True(stream.Closed);
    }
    private sealed class BlockingStream(byte[] prefix) : Stream
    {
        private int _offset; internal bool Closed;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < prefix.Length) { var count = Math.Min(buffer.Length, prefix.Length - _offset); prefix.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count; }
            await Task.Delay(Timeout.Infinite, ct); return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) Closed = true; base.Dispose(disposing); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void DuplicateItemIdsRemainBoundToTheExactAccount()
    {
        var history = new CodexResponseHistory(new());
        var first = Completed(); first["output"]![0]!["content"]![0]!["text"] = "account-a";
        var second = Completed(); second["output"]![0]!["content"]![0]!["text"] = "account-b";
        var request = new JsonObject { ["input"] = new JsonArray() };
        history.Remember("a", request, first); history.Remember("b", request, second);
        var reference = new JsonObject { ["input"] = new JsonArray(new JsonObject { ["type"] = "item_reference", ["id"] = "msg_fixture" }) };
        Assert.Equal("account-a", history.Expand("a", reference)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("account-b", history.Expand("b", reference)["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
        history.Clear(); Assert.Throws<SdkException>(() => history.Expand("a", reference));
    }

    [Theory]
    [InlineData("failed")] [InlineData("cancelled")]
    public async Task FailedTerminalDoesNotBecomeSuccessfulChat(string status)
    {
        var completed = Completed(); completed["status"] = status;
        using var upstream = new Handler((_, _) => Task.FromResult(Events(new JsonObject { ["type"] = "response." + status, ["response"] = completed })));
        using var client = Client(upstream); using var request = Request(new() { ["model"] = "gpt-6", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) }, path:"chat/completions");
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); Assert.Equal(1, upstream.Calls);
    }
}
