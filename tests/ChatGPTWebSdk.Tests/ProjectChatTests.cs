using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;

namespace ChatGPTWebSdk.Tests;

public sealed class ProjectChatTests
{
    private static readonly ConversationScope Scope = new("account", "alice", "project-thread");
    private const string Project = "g-p-synthetic-project";
    private static WebTurnRequest Turn(string? project = null, string? gizmo = null) => new() { Model = "gpt-6", Messages = [WebInputMessage.User("hello")], ProjectId = project, GizmoId = gizmo };

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Project_context_is_retained_when_later_turns_omit_it(bool useGizmo, bool useFileStore)
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-project-" + Guid.NewGuid());
        try
        {
            using var fake = new FakeWebHandler();
            IConversationStore store = useFileStore ? new FileConversationStore(directory) : new InMemoryConversationStore();
            var client = fake.Client(store);
            var first = await client.SendAsync(Scope, useGizmo ? Turn(gizmo: Project) : Turn(Project));
            await fake.Client(useFileStore ? new FileConversationStore(directory) : store).SendAsync(Scope, Turn());
            Assert.All(fake.Turns, sent => { Assert.Equal(Project, sent.Body["gizmo_id"]!.GetValue<string>()); Assert.Equal("gizmo_interaction", sent.Body["conversation_mode"]!["kind"]!.GetValue<string>()); });
            Assert.Equal(first.Response.ConversationId, fake.Turns[1].Body["conversation_id"]!.GetValue<string>());
            Assert.Equal(Project, (await client.GetStateAsync(Scope)).ProjectId);
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Theory]
    [InlineData(null)] [InlineData(Project)] [InlineData("g-synthetic-custom-gpt")]
    public async Task An_existing_thread_cannot_be_reassigned_to_another_project(string? initial)
    {
        using var fake = new FakeWebHandler(); var client = fake.Client();
        await client.SendAsync(Scope, Turn(gizmo: initial));
        var error = await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, Turn("g-p-other-project")));
        Assert.Equal("project_binding_conflict", error.Code);
        Assert.Single(fake.Turns);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Editing_and_regeneration_retain_project_context(bool edit)
    {
        using var fake = new FakeWebHandler(); var client = fake.Client();
        await client.SendAsync(Scope, Turn(Project));
        fake.Remote = fake.CompletedRemote(fake.Turns[0].Body); fake.Remote["gizmo_id"] = Project;
        if (edit) await client.EditAsync(Scope, fake.Turns[0].Body["messages"]![0]!["id"]!.GetValue<string>(), "edited", "gpt-6");
        else await client.RegenerateAsync(Scope, "gpt-6");
        Assert.Equal(Project, fake.Turns[1].Body["gizmo_id"]!.GetValue<string>());
        Assert.Equal(edit ? "next" : "variant", fake.Turns[1].Body["action"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Linked_remote_project_metadata_is_imported(bool nested)
    {
        using var fake = new FakeWebHandler();
        var seed = fake.Client(); await seed.SendAsync(Scope, Turn(Project));
        fake.Remote = fake.CompletedRemote(fake.Turns[0].Body);
        if (nested) fake.Remote["conversation_mode"] = new JsonObject { ["gizmo_id"] = Project };
        else fake.Remote["gizmo_id"] = Project;
        var client = fake.Client(); await client.LinkAsync(Scope, "conversation-1");
        await client.SendAsync(Scope, Turn());
        Assert.Equal(Project, fake.Turns[1].Body["gizmo_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("g-p-a/b")] [InlineData("g-p-a?query")] [InlineData("g-p-a#fragment")]
    [InlineData("g-p-a\r\nsecret")] [InlineData("g-p-a space")] [InlineData("g-p-ñ")]
    [InlineData("g-custom-gpt")] [InlineData("g-p-")] [InlineData("")]
    public async Task Invalid_project_ids_fail_before_a_generation_request(string id)
    {
        using var fake = new FakeWebHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => fake.Client().SendAsync(Scope, Turn(id)));
        Assert.Empty(fake.Turns);
    }

    [Fact]
    public async Task Conflicting_project_and_gizmo_ids_fail_before_send()
    {
        using var fake = new FakeWebHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => fake.Client().SendAsync(Scope, Turn(Project, "g-p-other")));
        Assert.Empty(fake.Turns);
    }

    [Fact]
    public async Task Linking_a_remote_temporary_chat_does_not_write_a_persistent_transcript()
    {
        using var fake = new FakeWebHandler();
        await fake.Client().SendAsync(Scope, Turn());
        fake.Remote = fake.CompletedRemote(fake.Turns[0].Body); fake.Remote["is_temporary_chat"] = true;
        var client = fake.Client();
        var error = await Assert.ThrowsAsync<SdkException>(() => client.LinkAsync(Scope, "conversation-1"));
        Assert.Equal("conversation_mode_conflict", error.Code);
        var state = await client.GetStateAsync(Scope);
        Assert.Null(state.ConversationId); Assert.Empty(state.History);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Project_and_temporary_context_cannot_be_combined(bool compatible)
    {
        using var fake = new FakeWebHandler(); var client = fake.Client();
        var error = compatible
            ? await Assert.ThrowsAsync<SdkException>(() => new OpenAiWebAdapter(client, defaultContext: new() { ProjectId = Project }).PrepareResponseAsync(Scope, new() { ["model"] = "gpt-6", ["input"] = "hello", ["store"] = false }))
            : await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope, new() { Model = "gpt-6", Messages = [WebInputMessage.User("hello")], TemporaryChat = true, ProjectId = Project }));
        Assert.Equal("temporary_project_conflict", error.Code); Assert.Empty(fake.Turns);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)] [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task HTTP_compatibility_context_applies_to_chat_responses_and_streaming(bool responses, bool stream, bool temporary)
    {
        using var fake = new FakeWebHandler(); var client = fake.Client();
        using var http = new HttpClient(new OpenAiWebHttpHandler(new(client), (_, thread) => new("account", "alice", thread ?? "default"))) { BaseAddress = new("https://compat.test/v1/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-client");
        http.DefaultRequestHeaders.Add("X-ChatGPT-Thread-Id", "context-thread");
        if (temporary) http.DefaultRequestHeaders.Add("X-ChatGPT-Temporary-Chat", "true");
        else http.DefaultRequestHeaders.Add("X-ChatGPT-Project-Id", Project);
        var body = new JsonObject { ["model"] = "gpt-6", ["stream"] = stream };
        if (responses) body["input"] = "hello";
        else body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" });
        using var result = await http.PostAsJsonAsync(responses ? "responses" : "chat/completions", body);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        await result.Content.ReadAsStringAsync();
        var sent = Assert.Single(fake.Turns).Body;
        if (temporary) { Assert.True(sent["history_and_training_disabled"]!.GetValue<bool>()); Assert.False(sent["temporary_chat_requests_personalization"]!.GetValue<bool>()); }
        else Assert.Equal(Project, sent["gizmo_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Runtime_client_options_select_project_or_temporary_chat(bool temporary)
    {
        using var fake = new FakeWebHandler(); using var http = new HttpClient(fake);
        using var runtime = ChatGPTWeb.Initialize(new ChatGPTWebRuntimeOptions { Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }), AccountId = "account", UserId = "alice", Mode = ChatGPTWebMode.ApiOnly, HttpClient = http, Endpoints = new(), ConversationStore = new InMemoryConversationStore() });
        var chat = runtime.CreateClient(threadId: "selected", projectId: temporary ? null : Project, temporaryChat: temporary).GetChatClient("gpt-6");
        await chat.CompleteChatAsync("hello");
        var body = Assert.Single(fake.Turns).Body;
        if (temporary) Assert.True(body["history_and_training_disabled"]!.GetValue<bool>());
        else Assert.Equal(Project, body["gizmo_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("details", "/backend-api/gizmos/g-p-synthetic-project", "include_file_limits=true")]
    [InlineData("conversations", "/backend-api/gizmos/g-p-synthetic-project/conversations", "limit=20&owned_only=false")]
    [InlineData("connectors", "/backend-api/projects/g-p-synthetic-project/connector_scopes", "limit=20")]
    [InlineData("saves", "/backend-api/projects/g-p-synthetic-project/saves", "limit=20")]
    [InlineData("sidebar", "/backend-api/gizmos/snorlax/sidebar", "limit=20&conversations_per_gizmo=5&owned_only=false")]
    public async Task Project_operations_use_captured_endpoints_and_query_fields(string operation, string path, string query)
    {
        Uri? seen = null;
        using var http = new HttpClient(new RecordingHandler((request, _) => { seen = request.RequestUri; return Task.FromResult(FakeWebHandler.Json(new JsonObject())); }));
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await (operation switch { "details" => transport.GetProjectAsync("account", Project), "conversations" => transport.ListProjectConversationsAsync("account", Project), "connectors" => transport.ListProjectConnectorScopesAsync("account", Project), "saves" => transport.ListProjectSavesAsync("account", Project), _ => transport.ListProjectsAsync("account") });
        Assert.Equal(path, seen!.AbsolutePath); Assert.Equal("?" + query, seen.Query);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(101)] [InlineData(int.MaxValue)]
    public async Task Project_list_limits_are_validated_before_network_calls(int limit)
    {
        using var fake = new FakeWebHandler(); var transport = fake.Client().Transport;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.ListProjectsAsync("account", limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.ListProjectConversationsAsync("account", Project, limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.ListProjectConnectorScopesAsync("account", Project, limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.ListProjectSavesAsync("account", Project, limit));
    }
}
