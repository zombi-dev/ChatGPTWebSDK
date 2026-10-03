using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OpenAI;

public sealed record ApiOperation(string Id, string Method, string Path, string Summary,
    string[] RequiredPath, string[] RequiredQuery, string[] RequiredHeaders, bool RequiresBody,
    string[] RequestMediaTypes, string[] ResponseMediaTypes, bool Deprecated);

public sealed record ApiCatalog(string SpecSource, string SpecVersion, string Sha256, ApiOperation[] Operations)
{
    public static ApiCatalog Current { get; } = Load();
    private static ApiCatalog Load()
    {
        using var stream = typeof(ApiCatalog).Assembly.GetManifestResourceStream("ChatGPTWebSdk.Generated.openapi-operations.json")!;
        return JsonSerializer.Deserialize<ApiCatalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    public ApiOperation Get(string id) => Operations.FirstOrDefault(o => o.Id == id)
        ?? throw new ArgumentException($"Unknown OpenAPI operation '{id}'.", nameof(id));
}

public sealed class ApiRequest
{
    public Dictionary<string, string> Path { get; init; } = [];
    public Dictionary<string, string?> Query { get; init; } = [];
    public Dictionary<string, string> Headers { get; init; } = [];
    public JsonNode? Body { get; init; }
    // Supports multipart files, audio, SDP and any media type defined by the spec. Ownership passes to the request.
    public HttpContent? Content { get; init; }
}

public sealed class ApiResult(HttpResponseMessage response) : IDisposable
{
    public HttpStatusCode StatusCode => response.StatusCode;
    public HttpResponseHeaders Headers => response.Headers;
    public HttpContentHeaders ContentHeaders => response.Content.Headers;
    public Task<JsonNode?> ReadJsonAsync(CancellationToken ct = default) => response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
    public Task<byte[]> ReadBytesAsync(CancellationToken ct = default) => response.Content.ReadAsByteArrayAsync(ct);
    public Task<Stream> ReadStreamAsync(CancellationToken ct = default) => response.Content.ReadAsStreamAsync(ct);
    public void Dispose() => response.Dispose();
}

public sealed class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, ValueTask<string>> _credential;
    private readonly Uri _baseUri;
    public OpenAiOperationsClient Operations { get; }
    public OpenAiResponses Responses { get; }
    public OpenAiChat Chat { get; }
    public OpenAiFiles Files { get; }

    public OpenAiClient(HttpClient http, string apiKey, Uri? baseUri = null)
        : this(http, _ => ValueTask.FromResult(apiKey), baseUri) { }

    public OpenAiClient(HttpClient http, Func<CancellationToken, ValueTask<string>> credential, Uri? baseUri = null)
    {
        _http = http;
        _credential = credential;
        _baseUri = baseUri ?? new("https://api.openai.com/v1/");
        Operations = new(this);
        Responses = new(this);
        Chat = new(this);
        Files = new(this);
    }

    private async Task<HttpRequestMessage> BuildAsync(string operationId, ApiRequest? request, CancellationToken ct)
    {
        request ??= new();
        var op = ApiCatalog.Current.Get(operationId);
        var path = op.Path;
        foreach (var name in op.RequiredPath)
        {
            if (!request.Path.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Missing path parameter '{name}'.");
            if (value is "." or "..") throw new ArgumentException("Dot path segments are not allowed.");
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        if (path.Contains('{')) throw new ArgumentException("Unresolved path parameter.");
        foreach (var name in op.RequiredQuery)
            if (!request.Query.TryGetValue(name, out var value) || value is null) throw new ArgumentException($"Missing query parameter '{name}'.");
        foreach (var name in op.RequiredHeaders)
            if (!request.Headers.ContainsKey(name)) throw new ArgumentException($"Missing header '{name}'.");
        if (op.RequiresBody && request.Body is null && request.Content is null) throw new ArgumentException("This operation requires a body.");
        if (request.Body is not null && request.Content is not null) throw new ArgumentException("Use Body or Content, not both.");
        if (request.Body is not null && !op.RequestMediaTypes.Contains("application/json")) throw new ArgumentException("This operation requires non-JSON Content.");
        var query = string.Join("&", request.Query.Where(q => q.Value is not null).Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value!)));
        if (query.Length > 0) path += (path.Contains('?') ? "&" : "?") + query;
        var token = await _credential(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token)) throw new SdkException("An API key is required for the official API transport.", "authentication_required");
        var message = new HttpRequestMessage(new HttpMethod(op.Method), HttpProtocol.RelativeUri(_baseUri, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if ((op.Path.StartsWith("/assistants", StringComparison.Ordinal) || op.Path.StartsWith("/threads", StringComparison.Ordinal)) &&
            !request.Headers.Keys.Any(k => k.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase)))
            message.Headers.Add("OpenAI-Beta", "assistants=v2");
        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Authentication and Host cannot be overridden.");
            message.Headers.Add(header.Key, header.Value);
        }
        message.Content = request.Content ?? (request.Body is null ? null : JsonContent.Create(request.Body));
        return message;
    }

    public async Task<ApiResult> SendAsync(string operationId, ApiRequest? request = null, CancellationToken ct = default)
    {
        using var message = await BuildAsync(operationId, request, ct).ConfigureAwait(false);
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try { await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false); return new(response); }
        catch { response.Dispose(); throw; }
    }

    public async IAsyncEnumerable<ServerSentEvent> StreamAsync(string operationId, ApiRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var result = await SendAsync(operationId, request, ct).ConfigureAwait(false);
        if (result.ContentHeaders.ContentType?.MediaType != "text/event-stream") throw new SdkException("Expected an SSE stream.", "unexpected_content_type");
        await using var stream = await result.ReadStreamAsync(ct).ConfigureAwait(false);
        await foreach (var item in ServerSentEvents.ReadAsync(stream, cancellationToken: ct).ConfigureAwait(false)) yield return item;
    }
}

public sealed partial class OpenAiOperationsClient(OpenAiClient client)
{
    private readonly OpenAiClient _client = client;
}

