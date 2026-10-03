using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

public enum WebResponseKind { Json, EventStream, Binary }

/// <summary>Sanitized request metadata observed in the supplied HAR. Observed HTTP failures do not establish successful backend support.</summary>
public sealed record WebCapturedOperation
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<string> QueryParameters { get; init; }
    public required IReadOnlyList<string> BodyFields { get; init; }
    public required WebResponseKind ResponseKind { get; init; }
    public required int CapturedRequests { get; init; }
    public required IReadOnlyList<int> CapturedStatuses { get; init; }
}

/// <summary>Calls every captured ChatGPT service operation without replaying captured credentials, IDs or private payloads.</summary>
public sealed class WebCapturedOperationsClient(ChatGptWebTransport transport)
{
    public static IReadOnlyList<WebCapturedOperation> Operations { get; } = Load();
    public static WebCapturedOperation GetOperation(string id) => Operations.FirstOrDefault(o => o.Id == id)
        ?? throw new ArgumentException("Unknown captured operation ID.", nameof(id));
    /// <summary>Builds a relative request path for inspection without reading credentials or contacting the service.</summary>
    public static string GetRequestPath(string operationId, IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyDictionary<string, string?>? query = null) =>
        Path(GetOperation(operationId), parameters, query);

    public async Task<JsonNode?> SendJsonAsync(string account, string operationId,
        IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyDictionary<string, string?>? query = null,
        JsonNode? body = null, CancellationToken ct = default)
    {
        var operation = GetOperation(operationId);
        if (operation.ResponseKind != WebResponseKind.Json) throw new ArgumentException("Use SendAsync for binary content or StreamAsync for server-sent events.", nameof(operationId));
        using var response = await SendAsync(account, operationId, parameters, query, body, ct).ConfigureAwait(false);
        return await HttpProtocol.ReadNullableJsonAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Returns an owned response. Dispose it after reading. Generation uses StreamAsync so its fresh Sentinel exchange cannot be skipped.</summary>
    public Task<HttpResponseMessage> SendAsync(string account, string operationId,
        IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyDictionary<string, string?>? query = null,
        JsonNode? body = null, CancellationToken ct = default)
    {
        var operation = GetOperation(operationId);
        if (operation.Id == "GenerateConversation") throw new ArgumentException("Generation requires StreamAsync or ChatGptWebClient to obtain fresh Sentinel authorization.", nameof(operationId));
        return transport.SendCapturedResponseAsync(account, new HttpMethod(operation.Method), Path(operation, parameters, query), body,
            operation.Id != "GetAuthSession", ct);
    }

    public async IAsyncEnumerable<ServerSentEvent> StreamAsync(string account, string operationId,
        IReadOnlyDictionary<string, string>? parameters = null, IReadOnlyDictionary<string, string?>? query = null,
        JsonNode? body = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var operation = GetOperation(operationId);
        if (operation.ResponseKind != WebResponseKind.EventStream) throw new ArgumentException("This captured operation does not return server-sent events.", nameof(operationId));
        _ = Path(operation, parameters, query);
        if (operation.Id == "GenerateConversation")
        {
            using var turn = await transport.OpenTurnAsync(account, body as JsonObject ?? throw new ArgumentException("Generation requires a JSON object body.", nameof(body)), ct).ConfigureAwait(false);
            await foreach (var item in turn.ReadAsync(ct).ConfigureAwait(false)) yield return item;
        }
        else
        {
            using var response = await SendAsync(account, operationId, parameters, query, body, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new SdkException("Expected a captured event stream.", "unexpected_content_type");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var item in ServerSentEvents.ReadAsync(stream, transport.MaxEventCharacters, ct).ConfigureAwait(false)) yield return item;
        }
    }

    internal static string Path(WebCapturedOperation operation, IReadOnlyDictionary<string, string>? parameters, IReadOnlyDictionary<string, string?>? query)
    {
        var names = Regex.Matches(operation.Path, "\\{([a-z_]+)\\}").Select(m => m.Groups[1].Value).ToArray();
        if (parameters is not null && parameters.Keys.Any(k => !names.Contains(k, StringComparer.Ordinal))) throw new ArgumentException("Unknown captured path parameter.", nameof(parameters));
        var path = operation.Path;
        foreach (var name in names)
        {
            if (parameters is null || !parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(char.IsControl))
                throw new ArgumentException("A captured path parameter is missing or invalid.", nameof(parameters));
            if (name == "project_id") WebChatContext.ValidateProjectId(value);
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        if (query is null || query.Count == 0) return path;
        if (query.Keys.Any(k => !operation.QueryParameters.Contains(k, StringComparer.Ordinal))) throw new ArgumentException("Unknown captured query parameter.", nameof(query));
        var pairs = query.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!));
        var encoded = string.Join("&", pairs);
        return encoded.Length == 0 ? path : path + "?" + encoded;
    }

    private static ReadOnlyCollection<WebCapturedOperation> Load()
    {
        using var stream = typeof(WebCapturedOperationsClient).Assembly.GetManifestResourceStream("ChatGPTWebSdk.Generated.web-operations.json")!;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var operations = JsonSerializer.Deserialize<WebCapturedOperation[]>(stream, options)!;
        return Array.AsReadOnly(operations.Select(o => o with { QueryParameters = Array.AsReadOnly(o.QueryParameters.ToArray()),
            BodyFields = Array.AsReadOnly(o.BodyFields.ToArray()), CapturedStatuses = Array.AsReadOnly(o.CapturedStatuses.ToArray()) }).ToArray());
    }
}

public sealed partial class ChatGptWebTransport
{
    public WebCapturedOperationsClient CapturedOperations => new(this);
    internal int MaxEventCharacters => _options.MaxEventCharacters;
    internal async Task<HttpResponseMessage> SendCapturedResponseAsync(string account, HttpMethod method, string path, JsonNode? body, bool authorize, CancellationToken ct)
    {
        var credential = authorize ? await GetCredentialsAsync(account, ct).ConfigureAwait(false) : await credentials.GetAsync(account, ct).ConfigureAwait(false);
        using var request = Build(credential, method, path, body, authorize);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try { await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false); return response; }
        catch { response.Dispose(); throw; }
    }
}
