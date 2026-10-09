using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

internal static class TestDirectory
{
    public static void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("websdk-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside this test's temporary output.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}

public sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
}

public sealed record RecordedTurn(JsonObject Body, string? Authorization);

public sealed class FakeWebHandler : HttpMessageHandler
{
    private int _turn;
    public List<RecordedTurn> Turns { get; } = [];
    public JsonObject Requirements { get; set; } = new() { ["token"] = "fixture-requirements" };
    public Func<JsonObject, int, string>? StreamFactory { get; set; }
    public JsonNode? Remote { get; set; }
    public HttpStatusCode? TurnFailure { get; set; }
    public Func<JsonObject, HttpStatusCode?>? TurnFailureSelector { get; set; }
    public JsonArray ModelCatalog { get; set; } = new(new JsonObject { ["slug"] = "gpt-6" });
    public int DelayMilliseconds { get; set; }
    public List<string> DeletedConversations { get; } = [];
    public static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK) { Content = JsonContent.Create(node) };
    public static string Event(JsonNode data) => "data: " + data.ToJsonString() + "\n\n";
    public static JsonObject Snapshot(string conversation, string messageId, string text, bool finished) => new()
    {
        ["conversation_id"] = conversation,
        ["message"] = new JsonObject { ["id"] = messageId, ["author"] = new JsonObject { ["role"] = "assistant" },
            ["recipient"] = "all", ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(JsonValue.Create(text)) },
            ["status"] = finished ? "finished_successfully" : "in_progress", ["end_turn"] = finished }
    };
    public static string NormalStream(JsonObject body, int index)
    {
        var conversation = body["conversation_id"]?.GetValue<string>() ?? "conversation-" + index;
        return Event(Snapshot(conversation, "assistant-" + index, "Hello", false)) +
            Event(Snapshot(conversation, "assistant-" + index, "Hello world", true)) + "data: [DONE]\n\n";
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("chat-requirements")) return Json(Requirements.DeepClone());
        if (path.EndsWith("/models")) return Json(new JsonObject { ["models"] = ModelCatalog.DeepClone() });
        if (path == "/api/auth/session") return Json(new JsonObject { ["accessToken"] = "refreshed-fixture-token", ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        if (request.Method == HttpMethod.Post && path.EndsWith("/conversation"))
        {
            var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!;
            int index;
            lock (Turns)
            {
                index = ++_turn;
                Turns.Add(new((JsonObject)body.DeepClone(), request.Headers.Authorization?.Parameter));
            }
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, ct);
            if ((TurnFailureSelector?.Invoke(body) ?? TurnFailure) is { } status) return new(status) { Content = new StringContent("fixture upstream error") };
            return new(HttpStatusCode.OK) { Content = new StringContent((StreamFactory ?? NormalStream)(body, index), Encoding.UTF8, "text/event-stream") };
        }
        if (request.Method == HttpMethod.Get && path.Contains("/conversation/")) return Json(Remote?.DeepClone() ?? throw new InvalidOperationException("Missing remote fixture."));
        if (request.Method == HttpMethod.Patch) return Json(new JsonObject { ["success"] = true });
        if (request.Method == HttpMethod.Delete && path.StartsWith("/backend-api/conversation/id/", StringComparison.Ordinal))
        { DeletedConversations.Add(path.Split('/')[^1]); return Json(new JsonObject { ["success"] = true }); }
        return new(HttpStatusCode.NotFound);
    }
    public ChatGptWebClient Client(IConversationStore? store = null, WebClientOptions? options = null)
    {
        var http = new HttpClient(this, disposeHandler: false);
        var auth = new StaticWebCredentialProvider(new Dictionary<string, WebCredentials>
        {
            ["account"] = new() { AccessToken = "fixture-token" },
            ["other-account"] = new() { AccessToken = "other-fixture-token" }
        });
        return new(new(http, auth, options), store ?? new InMemoryConversationStore());
    }

    public JsonObject CompletedRemote(JsonObject sent, int index = 1)
    {
        var inputId = sent["messages"]![0]!["id"]!.GetValue<string>();
        var userText = sent["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>();
        return new()
        {
            ["current_node"] = "assistant-" + index,
            ["mapping"] = new JsonObject
            {
                ["root"] = new JsonObject { ["parent"] = null, ["message"] = null },
                [inputId] = new JsonObject { ["parent"] = "root", ["message"] = new JsonObject { ["id"] = inputId,
                    ["author"] = new JsonObject { ["role"] = "user" }, ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(JsonValue.Create(userText)) } } },
                ["assistant-" + index] = new JsonObject { ["parent"] = inputId, ["message"] = Snapshot("conversation-" + index, "assistant-" + index, "Hello world", true)["message"]!.DeepClone() }
            }
        };
    }
}
