using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.OpenAI;

public sealed class ResponseRequest
{
    public string? Model { get; init; }
    public required JsonNode Input { get; init; }
    public string? Instructions { get; init; }
    public string? Conversation { get; init; }
    public string? PreviousResponseId { get; init; }
    public bool Stream { get; init; }
    public Dictionary<string, JsonNode?> Extra { get; init; } = [];
    public JsonObject ToJson()
    {
        var result = new JsonObject { ["input"] = Input.DeepClone(), ["stream"] = Stream };
        if (Model is not null) result["model"] = Model;
        if (Instructions is not null) result["instructions"] = Instructions;
        if (Conversation is not null) result["conversation"] = Conversation;
        if (PreviousResponseId is not null) result["previous_response_id"] = PreviousResponseId;
        if (Conversation is not null && PreviousResponseId is not null) throw new ArgumentException("conversation and previous_response_id are mutually exclusive.");
        foreach (var pair in Extra)
        {
            if (result.ContainsKey(pair.Key)) throw new ArgumentException($"Extra cannot override '{pair.Key}'.");
            result[pair.Key] = pair.Value?.DeepClone();
        }
        return result;
    }
}

public sealed record ChatMessage(string Role, string Content);

public sealed class ChatCompletionRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public bool Stream { get; init; }
    public JsonObject Extra { get; init; } = new();
    public JsonObject ToJson()
    {
        var body = (JsonObject)Extra.DeepClone();
        if (body.ContainsKey("model") || body.ContainsKey("messages") || body.ContainsKey("stream")) throw new ArgumentException("Extra cannot override core fields.");
        body["model"] = Model;
        body["messages"] = JsonSerializer.SerializeToNode(Messages, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        body["stream"] = Stream;
        return body;
    }
}

public sealed class OpenAiResponses(OpenAiClient client)
{
    public async Task<JsonNode> CreateAsync(ResponseRequest request, CancellationToken ct = default)
    {
        if (request.Stream) throw new ArgumentException("Use StreamAsync for streamed responses.");
        using var result = await client.Operations.CreateResponseAsync(new() { Body = request.ToJson() }, ct).ConfigureAwait(false);
        return (await result.ReadJsonAsync(ct).ConfigureAwait(false))!;
    }
    public IAsyncEnumerable<Protocol.ServerSentEvent> StreamAsync(ResponseRequest request, CancellationToken ct = default)
    {
        var body = request.ToJson();
        body["stream"] = true;
        return client.StreamAsync("createResponse", new() { Body = body }, ct);
    }
    public async Task<JsonNode> GetAsync(string id, CancellationToken ct = default)
    {
        using var result = await client.Operations.GetResponseAsync(new() { Path = new() { ["response_id"] = id } }, ct).ConfigureAwait(false);
        return (await result.ReadJsonAsync(ct).ConfigureAwait(false))!;
    }
}

public sealed class OpenAiChat(OpenAiClient client)
{
    public async Task<JsonNode> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        if (request.Stream) throw new ArgumentException("Use StreamAsync for streamed completions.");
        using var result = await client.Operations.CreateChatCompletionAsync(new() { Body = request.ToJson() }, ct).ConfigureAwait(false);
        return (await result.ReadJsonAsync(ct).ConfigureAwait(false))!;
    }
    public IAsyncEnumerable<Protocol.ServerSentEvent> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var body = request.ToJson();
        body["stream"] = true;
        return client.StreamAsync("createChatCompletion", new() { Body = body }, ct);
    }
}

public sealed class OpenAiFiles(OpenAiClient client)
{
    public Task<ApiResult> UploadAsync(Stream file, string fileName, string purpose, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        form.Add(new StringContent(purpose), "purpose");
        return client.Operations.CreateFileAsync(new() { Content = form }, ct);
    }
    public Task<ApiResult> DownloadAsync(string id, CancellationToken ct = default) =>
        client.Operations.DownloadFileAsync(new() { Path = new() { ["file_id"] = id } }, ct);
}

