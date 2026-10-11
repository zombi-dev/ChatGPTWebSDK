// Request normalization and terminal SSE handling adapted from EvanZhouDev/openai-oauth (Apache-2.0).
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

/// <summary>Opt-in Codex OAuth HTTP transport. Creates no ChatGPT website conversations and uses no Sentinel browser.</summary>
public sealed class CodexOAuthHttpHandler : HttpMessageHandler
{
    private readonly IOAuthSessionProvider _sessions;
    private readonly CodexOAuthOptions _options;
    private readonly HttpClient _upstream;
    private readonly bool _ownsHttp;
    private readonly CodexResponseHistory _history;
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expiry, JsonArray Models)> _catalogs = new();
    public CodexOAuthHttpHandler(IOAuthSessionProvider sessions, CodexOAuthOptions? options = null, HttpClient? upstream = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions)); _options = options ?? new(); _options.Validate();
        _history = new(_options); _ownsHttp = upstream is null;
        _upstream = upstream ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(30) })
            { Timeout = Timeout.InfiniteTimeSpan };
    }
    protected override void Dispose(bool disposing) { if (disposing) { _history.Clear(); if (_ownsHttp) _upstream.Dispose(); } base.Dispose(disposing); }
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct).ConfigureAwait(false).GetAwaiter().GetResult();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await DispatchAsync(request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var sdk = ex as SdkException;
            var invalid = ex is ArgumentException or JsonException or InvalidOperationException;
            return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = sdk?.Message ?? (invalid ? "Invalid Codex request." : "Codex request failed. No generation was retried."),
                ["type"] = sdk?.Code ?? "codex_request_failed", ["code"] = sdk?.Code ?? "codex_request_failed" } }, sdk?.StatusCode ?? (invalid || sdk is not null ? HttpStatusCode.BadRequest : HttpStatusCode.BadGateway));
        }
    }
    private Uri Endpoint(string path) => new(_options.BaseUri.AbsoluteUri.TrimEnd('/') + "/" + path);
    private async Task<HttpResponseMessage> SendUpstreamAsync(HttpMethod method, string path, OAuthSession session, JsonObject? body, CancellationToken ct, bool responsesLite = false)
    {
        using var request = new HttpRequestMessage(method, Endpoint(path));
        request.Headers.Authorization = new("Bearer", session.AccessToken);
        request.Headers.Add("chatgpt-account-id", session.AccountId);
        request.Headers.Accept.Add(new(path == "responses" ? "text/event-stream" : "application/json"));
        if (session.IsFedRamp) request.Headers.Add("X-OpenAI-Fedramp", "true");
        if (responsesLite) request.Headers.Add("x-openai-internal-codex-responses-lite", "true");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode; response.Dispose();
            throw new SdkException("Codex rejected the request. Check sign-in, model access and account limits.", "codex_upstream_rejected", status);
        }
        return response;
    }
    private async Task<JsonArray> CatalogAsync(OAuthSession session, CancellationToken ct)
    {
        var key = session.AccountId + ":" + session.IsFedRamp;
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogs.TryGetValue(key, out var entry) && entry.Expiry > DateTimeOffset.UtcNow) return (JsonArray)entry.Models.DeepClone();
            using var response = await SendUpstreamAsync(HttpMethod.Get, "models?client_version=" + Uri.EscapeDataString(_options.ClientVersion), session, null, ct).ConfigureAwait(false);
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (root?["models"] is not JsonArray models) throw new SdkException("Codex returned an invalid model catalog.", "codex_models_invalid");
            var selected = new JsonArray(models.OfType<JsonObject>().Where(m => m["supported_in_api"]?.GetValue<bool>() != false &&
                (m["visibility"] is null || m["visibility"]?.GetValue<string>() == "list")).Select(m => m.DeepClone()).ToArray());
            if (_catalogs.Count >= 32) _catalogs.Remove(_catalogs.Keys.First());
            _catalogs[key] = (DateTimeOffset.UtcNow + _options.ModelCacheDuration, selected); return (JsonArray)selected.DeepClone();
        }
        finally { _modelGate.Release(); }
    }

    private async Task<HttpResponseMessage> DispatchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.StartsWith("/v1/", StringComparison.Ordinal)) path = path[3..];
        var chat = path == "/chat/completions";
        var image = path is "/images/generations" or "/images/edits";
        if (!(request.Method == HttpMethod.Get && (path == "/models" || path.StartsWith("/models/", StringComparison.Ordinal))) &&
            !(request.Method == HttpMethod.Post && (path == "/responses" || chat || image)))
            throw new SdkException("This operation is unavailable on the stateless Codex transport. Use the ChatGPT web runtime for website conversations and assets.", "codex_operation_unsupported", HttpStatusCode.NotImplemented);
        var session = await _sessions.GetSessionAsync(ct).ConfigureAwait(false); session.Validate();
        if (image)
        {
            var imageBody = await CodexImages.PrepareAsync(request.Content ?? throw new ArgumentException("Image request body is required."), path == "/images/edits", ct).ConfigureAwait(false);
            using var result = await SendUpstreamAsync(HttpMethod.Post, path.TrimStart('/'), session, imageBody, ct).ConfigureAwait(false);
            return Json(JsonNode.Parse(await result.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? throw new JsonException("Empty image response."));
        }
        if (request.Method == HttpMethod.Get)
        {
            var data = new JsonArray((await CatalogAsync(session, ct).ConfigureAwait(false)).OfType<JsonObject>().Select(m => (JsonNode)new JsonObject
                { ["id"] = m["slug"]?.GetValue<string>(), ["object"] = "model", ["created"] = 0, ["owned_by"] = "openai" }).ToArray());
            return path == "/models" ? Json(new JsonObject { ["object"] = "list", ["data"] = data })
                : Json(data.FirstOrDefault(m => m?["id"]?.GetValue<string>() == Uri.UnescapeDataString(path[8..]))?.DeepClone()
                    ?? throw new SdkException("Codex model not found.", "model_not_found", HttpStatusCode.NotFound));
        }
        var body = JsonNode.Parse(await (request.Content ?? throw new ArgumentException("Request body is required.")).ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject
            ?? throw new ArgumentException("Request body must be an object.");
        var wantsStream = body["stream"]?.GetValue<bool>() == true;
        var includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() == true;
        if (chat) body = CodexChatAdapter.Request(body);
        if (body["conversation"] is not null || body["background"]?.GetValue<bool>() == true)
            throw new SdkException("Codex OAuth supports stateless foreground requests. Website conversations and background generation use different contracts.", "codex_operation_unsupported", HttpStatusCode.NotImplemented);
        body["instructions"] ??= _options.Instructions;
        if (body["input"] is JsonValue input && input.TryGetValue<string>(out var text))
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }) });
        if (body["input"] is not JsonArray) throw new ArgumentException("Codex input must be a string or array.");
        body["store"] = false; body["stream"] = true; body.Remove("max_output_tokens");
        var include = body["include"] as JsonArray ?? new();
        if (!include.Any(n => n?.GetValue<string>() == "reasoning.encrypted_content")) include.Add("reasoning.encrypted_content");
        body["include"] = include;
        JsonObject? modelInfo = null;
        if (_options.DiscoverModelCapabilities)
        {
            try { modelInfo = (await CatalogAsync(session, ct).ConfigureAwait(false)).OfType<JsonObject>().FirstOrDefault(m => m["slug"]?.GetValue<string>() == body["model"]?.GetValue<string>()); }
            catch (Exception ex) when (ex is SdkException or JsonException or HttpRequestException) { /* Discovery is advisory; the generation endpoint still decides model access. */ }
        }
        var responsesLite = modelInfo?["use_responses_lite"]?.GetValue<bool>() == true;
        if (modelInfo?["default_reasoning_level"] is { } defaultEffort)
        { var reasoning = body["reasoning"] as JsonObject ?? new(); reasoning["effort"] ??= defaultEffort.DeepClone(); body["reasoning"] = reasoning; }
        if (modelInfo?["support_verbosity"]?.GetValue<bool>() == true && modelInfo["default_verbosity"] is { } defaultVerbosity)
        { var settings = body["text"] as JsonObject ?? new(); settings["verbosity"] ??= defaultVerbosity.DeepClone(); body["text"] = settings; }
        if (responsesLite)
        {
            var reasoning = body["reasoning"] as JsonObject ?? new(); reasoning["context"] = "all_turns"; body["reasoning"] = reasoning;
            var prefix = new JsonArray();
            if (body["tools"] is JsonArray { Count: > 0 } tools) prefix.Add(new JsonObject { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = tools.DeepClone() });
            if (body["instructions"]?.GetValue<string>() is { Length: > 0 } instructions)
                prefix.Add(new JsonObject { ["role"] = "developer", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = instructions }) });
            body["input"] = new JsonArray(prefix.Concat(body["input"]!.AsArray()).Select(n => n?.DeepClone()).ToArray());
            body["instructions"] = ""; body["parallel_tool_calls"] = false; body.Remove("tools");
        }
        var thread = request.Headers.TryGetValues("X-ChatGPT-Thread-Id", out var threads) ? threads.Single() : "default";
        var scope = JsonSerializer.Serialize(new[] { session.AccountId, _options.UserId, thread });
        body = _history.Expand(scope, body);
        var upstream = await SendUpstreamAsync(HttpMethod.Post, "responses", session, body, ct, responsesLite).ConfigureAwait(false);
        var events = EventsAsync(upstream, scope, body, ct);
        if (wantsStream)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            events = EventsAsync(upstream, scope, body, lifetime.Token);
            if (chat) events = CodexChatAdapter.Stream(events, body["model"]?.GetValue<string>() ?? "", includeUsage, lifetime.Token);
            var iterator = events.GetAsyncEnumerator(lifetime.Token);
            try
            {
                if (!await iterator.MoveNextAsync().ConfigureAwait(false)) throw new SdkException("Codex returned no stream events.", "codex_stream_incomplete");
                var content = new StreamContent(new CodexEventStream(iterator, iterator.Current, lifetime, upstream)); content.Headers.ContentType = new("text/event-stream");
                return new(HttpStatusCode.OK) { Content = content };
            }
            catch { try { await iterator.DisposeAsync().ConfigureAwait(false); } finally { lifetime.Dispose(); upstream.Dispose(); } throw; }
        }
        JsonObject? completed = null;
        await foreach (var item in events.ConfigureAwait(false))
        {
            if (JsonNode.Parse(item.Data)?["response"] is JsonObject response) completed = response;
        }
        if (chat && completed?["status"]?.GetValue<string>() is "failed" or "cancelled" or "canceled")
            throw new SdkException("Codex did not complete the chat response.", "codex_stream_failed", HttpStatusCode.BadGateway);
        return Json(completed is null ? throw new SdkException("Codex returned no terminal response.", "codex_stream_incomplete") : chat ? CodexChatAdapter.Response(completed) : completed);
    }

    private async IAsyncEnumerable<ServerSentEvent> EventsAsync(HttpResponseMessage response, string scope, JsonObject body, [EnumeratorCancellation] CancellationToken ct)
    {
        using (response)
        {
            var output = new Dictionary<string, (int Index, JsonObject Item)>();
            var outputCharacters = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var item in ServerSentEvents.ReadAsync(stream, cancellationToken: ct).ConfigureAwait(false))
            {
                if (item.Data == "[DONE]") break;
                var node = JsonNode.Parse(item.Data) as JsonObject ?? throw new SdkException("Codex returned an invalid SSE event.", "codex_stream_invalid");
                var type = node["type"]?.GetValue<string>() ?? item.Event;
                if (type == "error") throw new SdkException("Codex returned a stream error. No generation was retried.", "codex_stream_failed", HttpStatusCode.BadGateway);
                if (type == "response.output_item.done" && node["item"] is JsonObject completedItem && completedItem["id"]?.GetValue<string>() is { } itemId)
                {
                    if (output.Remove(itemId, out var old)) outputCharacters -= old.Item.ToJsonString().Length;
                    outputCharacters += completedItem.ToJsonString().Length;
                    if (outputCharacters > _options.MaxContextCharacters) throw new SdkException("Codex output exceeds its configured collection limit.", "codex_output_too_large");
                    output[itemId] = (node["output_index"]?.GetValue<int>() ?? output.Count, completedItem);
                }
                var terminal = type is "response.completed" or "response.failed" or "response.incomplete" or "response.cancelled" or "response.canceled";
                if (terminal && node["response"] is JsonObject result)
                {
                    if (result["output"] is not JsonArray { Count: > 0 } && output.Count > 0)
                        result["output"] = new JsonArray(output.Values.OrderBy(p => p.Index).Select(p => p.Item.DeepClone()).ToArray());
                    _history.Remember(scope, body, result); yield return new(node.ToJsonString(), item.Event, item.Id); yield break;
                }
                yield return item;
            }
            throw new SdkException("Codex stream ended without a terminal response. The generation was not retried.", "codex_stream_incomplete", HttpStatusCode.BadGateway);
        }
    }
    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
