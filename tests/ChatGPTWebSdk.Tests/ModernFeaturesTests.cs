using System.ClientModel;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Conversations;
using OpenAI.Files;

namespace ChatGPTWebSdk.Tests;

public sealed class ModernFeaturesTests
{
    private static readonly ConversationScope Scope = new("account", "alice");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAdUlEQVR4nO3PAQkAMAzAsIm9fwu7jDIIVEA68/Z2vcBALTBQCwzUAgO1wEAtMFALDNQCA7XAQC0wUAsM1AIDtcBALTBQCwzUAgO1wEAtMFALDNQCA7XAQC0wUAsM1AIDtcBALTBQCwzUAgO1wEAtMFALDOzpPp47MUsiR9jcAAAAAElFTkSuQmCC");
    private static ChatGPTWebRuntime Runtime(HttpMessageHandler handler, IConversationStore? store = null, IWebSentinelSessionProvider? sentinel = null) => new(new()
    {
        Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic-account-token" }), AccountId = "account", UserId = "alice",
        ClientKey = "alice-key", Clients = new Dictionary<string, ConversationScope> { ["alice-key"] = Scope, ["bob-key"] = new("account", "bob") },
        HttpClient = new(handler, false), ConversationStore = store ?? new InMemoryConversationStore(), Endpoints = new(),
        Mode = sentinel is null ? ChatGPTWebMode.ApiOnly : ChatGPTWebMode.Hybrid, SentinelSessionProvider = sentinel
    });

    [Fact]
    public async Task Modern_prepare_finalize_and_conduit_match_the_captured_protocol()
    {
        var requests = new List<(string Path, JsonObject Body, Dictionary<string, string> Headers)>();
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!;
            requests.Add((path, body, request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value))));
            if (path.EndsWith("chat-requirements/prepare")) return FakeWebHandler.Json(new JsonObject { ["prepare_token"] = "synthetic-prepare", ["proofofwork"] = new JsonObject { ["required"] = true }, ["turnstile"] = new JsonObject { ["required"] = true } });
            if (path.EndsWith("chat-requirements/finalize")) return FakeWebHandler.Json(new JsonObject { ["token"] = "synthetic-finalized" });
            if (path.EndsWith("conversation/prepare")) return FakeWebHandler.Json(new JsonObject { ["conduit_token"] = "synthetic-conduit" });
            return new(HttpStatusCode.OK) { Content = new StringContent(FakeWebHandler.NormalStream(body, 1), Encoding.UTF8, "text/event-stream") };
        });
        var credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic-account-token" });
        using var http = new HttpClient(handler);
        var client = new ChatGptWebClient(new(http, credentials, new() { Endpoints = WebEndpointProfile.Modern, SentinelChallengeProvider = new Answers() }), new InMemoryConversationStore());
        await client.SendAsync(Scope, "hello", "fixture-model");
        Assert.Equal(4, requests.Count);
        Assert.Equal("synthetic-prepare", requests[1].Body["prepare_token"]!.GetValue<string>());
        Assert.Equal("synthetic-proof", requests[1].Body["proofofwork"]!.GetValue<string>());
        Assert.Null(requests[2].Body["messages"]);
        Assert.Equal("hello", requests[2].Body["partial_query"]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.Equal("client-created-root", requests[3].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal("synthetic-finalized", requests[3].Headers["openai-sentinel-chat-requirements-token"]);
        Assert.Equal("synthetic-conduit", requests[3].Headers["x-conduit-token"]);
        Assert.Equal(requests[2].Headers["x-oai-turn-trace-id"], requests[3].Headers["x-oai-turn-trace-id"]);
    }
    private sealed class Answers : IWebSentinelChallengeProvider
    {
        public ValueTask<WebSentinelChallengeAnswers> GetAnswersAsync(WebSentinelChallengeContext context, CancellationToken ct = default) => ValueTask.FromResult(new WebSentinelChallengeAnswers { ProofToken = "synthetic-proof", TurnstileToken = "synthetic-turnstile" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_appended_turn_obtains_fresh_sentinel_answers_instead_of_reusing_a_captured_proof(bool prepareToken)
    {
        var proofHeaders = new List<string>(); var sessions = new FreshSessions(prepareToken);
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            Assert.EndsWith("/conversation", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains(prepareToken ? "OpenAI-Sentinel-Chat-Requirements-Prepare-Token" : "OpenAI-Sentinel-Chat-Requirements-Token"));
            Assert.False(request.Headers.Contains(prepareToken ? "OpenAI-Sentinel-Chat-Requirements-Token" : "OpenAI-Sentinel-Chat-Requirements-Prepare-Token"));
            proofHeaders.Add(request.Headers.GetValues("OpenAI-Sentinel-Proof-Token").Single());
            var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!;
            return new(HttpStatusCode.OK) { Content = new StringContent(FakeWebHandler.NormalStream(body, proofHeaders.Count), Encoding.UTF8, "text/event-stream") };
        });
        using var runtime = Runtime(handler, sentinel: sessions); var chat = runtime.CreateClient().GetChatClient("fixture-model");
        await chat.CompleteChatAsync("first"); await chat.CompleteChatAsync("second");
        Assert.Equal(2, sessions.Calls); Assert.Equal(new[] { "synthetic-proof-1", "synthetic-proof-2" }, proofHeaders);
    }
    private sealed class FreshSessions(bool prepareToken) : IWebSentinelSessionProvider
    {
        public int Calls { get; private set; }
        public ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject body, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new WebAuthorizedSession(credentials, new() { Token = "synthetic-requirements-" + Calls, IsPrepareToken = prepareToken, ProofToken = "synthetic-proof-" + Calls, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }));
        }
    }

    [Fact]
    public async Task Original_ChatClient_constructor_uses_initialization_without_launching_a_browser_in_ApiOnly()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        ChatGPTWeb.Configure(runtime);
        var client = new ChatClient("fixture-model", runtime.ClientKey);
        Assert.Equal("Hello world", (await client.CompleteChatAsync("hello")).Value.Content[0].Text);
    }

    [Fact]
    public async Task Temporary_chat_content_remote_ids_and_response_index_are_never_written_to_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-temporary-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler(); var client = handler.Client(new FileConversationStore(directory));
            var first = await client.SendAsync(Scope, new() { Model = "fixture-model", Messages = [WebInputMessage.User("private-marker-42")], TemporaryChat = true });
            await client.SendAsync(Scope, new() { Model = "fixture-model", Messages = [WebInputMessage.User("follow-up")], TemporaryChat = true });
            Assert.True(handler.Turns[0].Body["history_and_training_disabled"]!.GetValue<bool>());
            Assert.False(handler.Turns[0].Body["temporary_chat_requests_personalization"]!.GetValue<bool>());
            Assert.Null(handler.Turns[1].Body["temporary_chat_requests_personalization"]);
            Assert.Equal(first.Response.ConversationId, handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            { var data = File.ReadAllText(path); Assert.DoesNotContain("private-marker-42", data); Assert.DoesNotContain(first.Response.ConversationId, data); Assert.DoesNotContain(first.Response.Id, data); }
            Assert.Equal(first.Response.Id, (await client.GetResponseAsync(Scope, first.Response.Id)).Id);
            await client.DeleteResponseAsync(Scope, first.Response.Id);
            await Assert.ThrowsAsync<SdkException>(() => client.GetResponseAsync(Scope, first.Response.Id));
            await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, "persistent", "fixture-model"));
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task Native_edit_regeneration_and_delete_keep_the_remote_branch_and_local_binding_consistent()
    {
        using var handler = new FakeWebHandler(); var client = handler.Client();
        var first = await client.SendAsync(Scope, "original", "fixture-model");
        handler.Remote = handler.CompletedRemote(handler.Turns[0].Body);
        var edited = await client.EditAsync(Scope, first.Response.Input[0].Id, "edited", "fixture-model");
        Assert.Equal("root", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        Assert.DoesNotContain((await client.GetStateAsync(Scope)).History, m => m.Id == first.Response.MessageId);
        handler.Remote = handler.CompletedRemote(handler.Turns[1].Body, 2);
        var regenerated = await client.RegenerateAsync(Scope, "fixture-model");
        Assert.Equal("variant", handler.Turns[2].Body["action"]!.GetValue<string>());
        Assert.Empty(handler.Turns[2].Body["messages"]!.AsArray());
        Assert.Equal(edited.Response.Input[0].Id, handler.Turns[2].Body["parent_message_id"]!.GetValue<string>());
        var state = await client.GetStateAsync(Scope);
        Assert.Equal(2, state.History.Count); Assert.Equal(regenerated.Response.MessageId, state.ParentMessageId);
        await client.UpdateConversationAsync(Scope, delete: true);
        Assert.Equal(first.Response.ConversationId, Assert.Single(handler.DeletedConversations));
        state = await client.GetStateAsync(Scope); Assert.Null(state.ConversationId); Assert.Empty(state.History); Assert.False(state.RequiresReconciliation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sentinel_failure_keeps_503_for_the_official_nonstreaming_and_streaming_clients(bool streaming)
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler, sentinel: new Failure());
        var client = runtime.CreateClient().GetChatClient("fixture-model");
        var error = await Assert.ThrowsAsync<ClientResultException>(async () =>
        {
            if (streaming) { await foreach (var _ in client.CompleteChatStreamingAsync("hello")) { } }
            else await client.CompleteChatAsync("hello");
        });
        Assert.Equal(503, error.Status); Assert.Empty(handler.Turns); Assert.False((await runtime.Web.GetStateAsync(Scope)).RequiresReconciliation);
    }
    private sealed class Failure : IWebSentinelSessionProvider
    {
        public ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject body, CancellationToken ct = default) =>
            throw new SdkException("Synthetic browser error.", "sentinel_browser_failed", HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Official_file_client_upload_download_and_user_isolation_work_without_sending_cookies_to_asset_hosts()
    {
        bool uploaded = false, downloaded = false;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "files.openai.com")
            {
                Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
                if (request.Method == HttpMethod.Put) { Assert.Equal(Png, await request.Content!.ReadAsByteArrayAsync(ct)); uploaded = true; return new(HttpStatusCode.Created); }
                downloaded = true; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            }
            if (path == "/backend-api/files")
            { var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!; Assert.Equal("image/png", body["mime_type"]!.GetValue<string>()); return FakeWebHandler.Json(new JsonObject { ["file_id"] = "file_fixture", ["upload_url"] = "https://files.openai.com/upload" }); }
            if (path.EndsWith("process_upload_stream")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"event\":\"file.processing.started\"}\n{\"event\":\"file.processing.completed\"}\n", Encoding.UTF8, "text/event-stream") };
            if (path.Contains("/files/download/")) return FakeWebHandler.Json(new JsonObject { ["download_url"] = "https://files.openai.com/download" });
            return new(HttpStatusCode.NotFound);
        });
        using var runtime = Runtime(handler); var files = runtime.CreateClient().GetOpenAIFileClient();
        OpenAIFile file = await files.UploadFileAsync(new MemoryStream(Png), "pixel.png", FileUploadPurpose.Vision);
        Assert.Equal("file_fixture", file.Id); Assert.True(uploaded);
        Assert.Equal(Png, (await files.DownloadFileAsync(file.Id)).Value.ToArray()); Assert.True(downloaded);
        Assert.Single((await files.GetFilesAsync()).Value);
        Assert.Single((await files.GetFilesAsync(FilePurpose.Vision)).Value);
        Assert.Empty((await files.GetFilesAsync(FilePurpose.UserData)).Value);
        var denied = await Assert.ThrowsAsync<ClientResultException>(() => runtime.CreateClient("bob-key").GetOpenAIFileClient().GetFileAsync(file.Id));
        Assert.Equal(404, denied.Status);
    }

    [Fact]
    public async Task Official_conversation_client_creates_metadata_resources_and_prevents_cross_user_reads()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var conversations = runtime.CreateClient().GetConversationClient();
        ConversationResource conversation = await conversations.CreateConversationAsync(new ConversationCreationOptions { Metadata = { ["topic"] = "fixture" } });
        Assert.StartsWith("conv_web_", conversation.Id); Assert.Equal("fixture", conversation.Metadata["topic"]); Assert.Empty(handler.Turns);
        var denied = await Assert.ThrowsAsync<ClientResultException>(() => runtime.CreateClient("bob-key").GetConversationClient().GetConversationAsync(conversation.Id));
        Assert.Equal(404, denied.Status);
        Assert.True((await conversations.DeleteConversationAsync(conversation.Id)).Value.Deleted);
    }

    [Theory]
    [InlineData(false, true, "normal")]
    [InlineData(true, true, "normal")]
    [InlineData(false, false, "normal")]
    [InlineData(true, false, "normal")]
    [InlineData(false, true, "project")]
    [InlineData(true, true, "project")]
    [InlineData(false, false, "project")]
    [InlineData(true, false, "project")]
    [InlineData(false, true, "temporary")]
    [InlineData(true, true, "temporary")]
    [InlineData(false, false, "temporary")]
    [InlineData(true, false, "temporary")]
    public async Task Official_image_generation_and_edit_clients_return_downloaded_image_bytes(bool edit, bool finalText, string context)
    {
        JsonObject? turn = null;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "files.openai.com")
            {
                Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
                return request.Method == HttpMethod.Put ? new(HttpStatusCode.Created) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
            }
            if (path.EndsWith("chat-requirements")) return FakeWebHandler.Json(new JsonObject { ["token"] = "synthetic-requirements" });
            if (path == "/backend-api/files") return FakeWebHandler.Json(new JsonObject { ["file_id"] = "file_input", ["upload_url"] = "https://files.openai.com/upload" });
            if (path.EndsWith("process_upload_stream")) return new(HttpStatusCode.OK) { Content = new StringContent("{\"event\":\"file.processing.completed\"}\n", Encoding.UTF8, "text/event-stream") };
            if (path.Contains("/files/download/")) return FakeWebHandler.Json(new JsonObject { ["download_url"] = "https://files.openai.com/download" });
            if (path.EndsWith("/conversation"))
            {
                turn = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!;
                var asset = new JsonObject { ["conversation_id"] = "conversation-generated", ["message"] = new JsonObject
                {
                    ["id"] = "image-tool", ["author"] = new JsonObject { ["role"] = "tool" }, ["status"] = "finished_successfully",
                    ["content"] = new JsonObject { ["content_type"] = "multimodal_text", ["parts"] = new JsonArray(new JsonObject { ["content_type"] = "image_asset_pointer", ["asset_pointer"] = "sediment://file_generated", ["width"] = 1, ["height"] = 1, ["size_bytes"] = Png.Length }) }
                } };
                var events = FakeWebHandler.Event(asset) + (finalText ? FakeWebHandler.Event(FakeWebHandler.Snapshot("conversation-generated", "assistant-generated", "Done", true)) : "") + "data: [DONE]\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
            }
            return new(HttpStatusCode.NotFound);
        });
        using var runtime = Runtime(handler); var images = runtime.CreateClient(projectId: context == "project" ? "g-p-synthetic-project" : null, temporaryChat: context == "temporary").GetImageClient("fixture-model");
        var result = edit ? await images.GenerateImageEditAsync(new MemoryStream(Png), "pixel.png", "Make it blue") : await images.GenerateImageAsync("A blue circle");
        Assert.Equal(Png, result.Value.ImageBytes.ToArray());
        Assert.Equal(finalText ? "assistant-generated" : "image-tool", (await runtime.Web.GetStateAsync(Scope)).ParentMessageId);
        if (edit) Assert.Equal("multimodal_text", turn!["messages"]![0]!["content"]!["content_type"]!.GetValue<string>());
        if (context == "project") Assert.Equal("g-p-synthetic-project", turn!["gizmo_id"]!.GetValue<string>());
        if (context == "temporary") Assert.True(turn!["history_and_training_disabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Response_index_survives_restart_and_can_never_cross_an_application_user()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-index-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler(); var thread = new ConversationScope("account", "alice", "another-thread");
            var first = await handler.Client(new FileConversationStore(directory)).SendAsync(thread, "hello", "fixture-model");
            var client = handler.Client(new FileConversationStore(directory));
            Assert.Equal(thread, await client.ResolveResponseScopeAsync(Scope, first.Response.Id));
            await Assert.ThrowsAsync<SdkException>(() => client.ResolveResponseScopeAsync(new("account", "bob"), first.Response.Id));
        }
        finally { TestDirectory.Delete(directory); }
    }
}
