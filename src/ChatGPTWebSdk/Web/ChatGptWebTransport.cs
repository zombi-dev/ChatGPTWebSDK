using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

public sealed partial class ChatGptWebTransport(HttpClient http, IWebCredentialProvider credentials, WebClientOptions? options = null)
{
    private readonly WebClientOptions _options = options ?? new();
    private readonly ConcurrentDictionary<string, (string Fingerprint, WebCredentials Credentials)> _refreshed = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _authLocks = new();
    private readonly ConcurrentDictionary<string, (string Source, WebCredentials Credentials)> _browserCredentials = new();
    private static string CredentialFingerprint(WebCredentials value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.CookieHeader + "\n" + value.AccessToken)));

    private async Task<WebCredentials> GetCredentialsAsync(string account, CancellationToken ct)
    {
        var value = await credentials.GetAsync(account, ct).ConfigureAwait(false);
        if (_browserCredentials.TryGetValue(account, out var browser) && browser.Source == CredentialFingerprint(value) && (browser.Credentials.ExpiresAt is null || browser.Credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))) return browser.Credentials;
        if (!string.IsNullOrWhiteSpace(value.AccessToken) && (value.ExpiresAt is null || value.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))) return value;
        if (string.IsNullOrWhiteSpace(value.CookieHeader)) throw new SdkException("A current access token or session cookies are required.", "authentication_required");
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.CookieHeader)));
        var gate = _authLocks.GetOrAdd(account, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_refreshed.TryGetValue(account, out var cached) && cached.Fingerprint == fingerprint && cached.Credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return cached.Credentials;
            using var request = Build(value, HttpMethod.Get, _options.Endpoints.Session, null, authorize: false);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var session = await HttpProtocol.ReadJsonAsync(response, ct).ConfigureAwait(false);
            var token = session["accessToken"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token)) throw new SdkException("Session endpoint did not return an access token. Sign in and refresh the credentials.", "authentication_required");
            DateTimeOffset? expiry = DateTimeOffset.TryParse(session["expires"]?.GetValue<string>(), out var parsed) ? parsed : DateTimeOffset.UtcNow.AddMinutes(5);
            var refreshed = new WebCredentials { AccessToken = token, CookieHeader = value.CookieHeader, UserAgent = value.UserAgent, Headers = value.Headers, ExpiresAt = expiry, SentinelSession = value.SentinelSession };
            _refreshed[account] = (fingerprint, refreshed);
            return refreshed;
        }
        finally { gate.Release(); }
    }

    private HttpRequestMessage Build(WebCredentials value, HttpMethod method, string path, JsonNode? body, bool authorize = true)
    {
        var request = new HttpRequestMessage(method, HttpProtocol.RelativeUri(_options.BaseUri, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("User-Agent", value.UserAgent);
        request.Headers.Add("Origin", _options.BaseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.Referrer = _options.BaseUri;
        if (authorize && value.AccessToken is not null) request.Headers.Authorization = new("Bearer", value.AccessToken);
        if (!string.IsNullOrWhiteSpace(value.CookieHeader)) request.Headers.Add("Cookie", value.CookieHeader);
        foreach (var header in value.Headers) AddContextHeader(request, header.Key, header.Value);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static void AddContextHeader(HttpRequestMessage request, string name, string value)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Origin", StringComparison.OrdinalIgnoreCase) || name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Header '{name}' is managed by the transport.");
        request.Headers.Remove(name);
        request.Headers.Add(name, value);
    }

    public async Task<JsonNode> SendJsonAsync(string account, HttpMethod method, string relativePath, JsonNode? body = null, CancellationToken ct = default)
    {
        var value = await GetCredentialsAsync(account, ct).ConfigureAwait(false);
        using var request = Build(value, method, relativePath, body);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await HttpProtocol.ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public Task<JsonNode> GetModelsAsync(string account, CancellationToken ct = default) => SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.Models, ct: ct);
    public Task<JsonNode> GetAccountsAsync(string account, CancellationToken ct = default) => SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.Accounts, ct: ct);
    public Task<JsonNode> GetPromptLibraryAsync(string account, int offset = 0, int limit = 4, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.PromptLibrary + $"?offset={offset}&limit={limit}", ct: ct);
    public Task<JsonNode> GenerateTitleAsync(string account, string conversationId, string messageId, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Post, _options.Endpoints.GenerateTitle.Replace("{conversation_id}", Segment(conversationId), StringComparison.Ordinal), new JsonObject { ["message_id"] = messageId }, ct);
    public Task<JsonNode> ListSharesAsync(string account, string order = "created", CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.Shares + "?order=" + Uri.EscapeDataString(order), ct: ct);
    public Task<JsonNode> CreateShareAsync(string account, string conversationId, string currentNodeId, bool anonymous = true, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Post, _options.Endpoints.ShareCreate,
            new JsonObject { ["conversation_id"] = conversationId, ["current_node_id"] = currentNodeId, ["is_anonymous"] = anonymous }, ct);
    public Task<JsonNode> UpdateShareAsync(string account, string shareId, JsonObject settings, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Patch, _options.Endpoints.ShareById.Replace("{share_id}", Segment(shareId), StringComparison.Ordinal), settings, ct);
    public Task<JsonNode> RevokeShareAsync(string account, string shareId, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Delete, _options.Endpoints.ShareById.Replace("{share_id}", Segment(shareId), StringComparison.Ordinal), ct: ct);
    public Task<JsonNode> GetConversationAsync(string account, string id, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, ConversationPath(id), ct: ct);
    public Task<JsonNode> ListConversationsAsync(string account, int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.Conversations + $"?offset={offset}&limit={limit}", ct: ct);
    }
    public Task<JsonNode> RenameConversationAsync(string account, string id, string title, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Patch, ConversationPath(id), new JsonObject { ["title"] = title }, ct);
    public Task<JsonNode> ArchiveConversationAsync(string account, string id, bool archived = true, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Patch, ConversationPath(id), new JsonObject { ["is_archived"] = archived }, ct);
    public Task<JsonNode> DeleteConversationAsync(string account, string id, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Delete, _options.Endpoints.DeleteConversation.Replace("{conversation_id}", Segment(id), StringComparison.Ordinal), ct: ct);
    private string ConversationPath(string id)
    {
        return _options.Endpoints.ConversationById.Replace("{conversation_id}", Segment(id), StringComparison.Ordinal);
    }
    private static string Segment(string id) => !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") ? Uri.EscapeDataString(id) : throw new ArgumentException("Invalid endpoint identifier.");

    public JsonObject CreateTurnBody(string model, string parentMessageId, string? conversationId, IReadOnlyList<WebInputMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Specify a model slug returned by the web models endpoint.");
        if (messages.Count == 0 || messages.Any(m => m.Role != "user")) throw new UnsupportedWebFeatureException("non-user or empty web input");
        var body = (JsonObject)_options.Endpoints.ConversationDefaults.DeepClone();
        body["action"] = "next";
        body["model"] = model;
        body["parent_message_id"] = conversationId is null && _options.Endpoints.ConversationPrepare is not null ? "client-created-root" : parentMessageId;
        if (conversationId is null) body.Remove("conversation_id"); else body["conversation_id"] = conversationId;
        body["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id,
            ["author"] = new JsonObject { ["role"] = m.Role },
            ["content"] = m.Content?.DeepClone() ?? new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray(JsonValue.Create(m.Text)) },
            ["metadata"] = m.Metadata?.DeepClone() ?? new JsonObject()
        }).ToArray());
        if (!body.ContainsKey("conversation_mode")) body["conversation_mode"] = new JsonObject { ["kind"] = "primary_assistant" };
        return body;
    }

    public async Task<WebTurnStream> OpenTurnAsync(string account, JsonObject body, CancellationToken ct = default)
    {
        var value = await GetCredentialsAsync(account, ct).ConfigureAwait(false);
        JsonNode? requirements = null;
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_options.FetchRequirements && _options.SentinelSessionProvider is not null)
        {
            var source = await credentials.GetAsync(account, ct).ConfigureAwait(false);
            var authorized = await _options.SentinelSessionProvider.GetSessionAsync(account, value, (JsonObject)body.DeepClone(), ct).ConfigureAwait(false);
            if (!authorized.Sentinel.IsCurrent) throw new SdkException("The browser returned expired Sentinel credentials.", "expired_sentinel_session");
            value = new WebCredentials { AccessToken = authorized.Credentials.AccessToken, CookieHeader = authorized.Credentials.CookieHeader, UserAgent = authorized.Credentials.UserAgent,
                ExpiresAt = authorized.Credentials.ExpiresAt, Headers = authorized.Credentials.Headers, SentinelSession = authorized.Sentinel };
            _browserCredentials[account] = (CredentialFingerprint(source), new WebCredentials { AccessToken = value.AccessToken, CookieHeader = value.CookieHeader,
                UserAgent = value.UserAgent, ExpiresAt = value.ExpiresAt, Headers = value.Headers });
        }
        if (_options.FetchRequirements && value.SentinelSession is { IsCurrent: true } session)
            foreach (var pair in session.Headers()) extra[pair.Key] = pair.Value;
        else if (_options.FetchRequirements)
        {
            var requirementsBody = _options.RequirementsBodyProvider is null ? _options.Endpoints.RequirementsBody :
                await _options.RequirementsBodyProvider.GetBodyAsync(account, (JsonObject)body.DeepClone(), ct).ConfigureAwait(false);
            using var requirementsRequest = Build(value, HttpMethod.Post, _options.Endpoints.RequirementsPrepare ?? _options.Endpoints.Requirements, requirementsBody);
            using var requirementsResponse = await http.SendAsync(requirementsRequest, ct).ConfigureAwait(false);
            requirements = await HttpProtocol.ReadJsonAsync(requirementsResponse, ct).ConfigureAwait(false);
        }
        if (_options.RequestHeaderProvider is not null)
            foreach (var pair in await _options.RequestHeaderProvider.GetHeadersAsync(new(account, (JsonObject)body.DeepClone(), requirements?.DeepClone()), ct).ConfigureAwait(false)) extra[pair.Key] = pair.Value;
        WebSentinelChallengeAnswers? answers = null;
        if (requirements is not null && _options.SentinelChallengeProvider is not null)
        {
            answers = await _options.SentinelChallengeProvider.GetAnswersAsync(new(account, (JsonObject)body.DeepClone(), requirements.DeepClone()), ct).ConfigureAwait(false);
            foreach (var pair in answers.Headers()) extra[pair.Key] = pair.Value;
        }
        foreach (var (challenge, header) in new[] { ("turnstile", "OpenAI-Sentinel-Turnstile-Token"), ("proofofwork", "OpenAI-Sentinel-Proof-Token"), ("arkose", "OpenAI-Sentinel-Arkose-Token") })
            if (requirements?[challenge]?["required"]?.GetValue<bool>() == true && !extra.Any(h => h.Key.Equals(header, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Value)))
                throw new WebChallengeException(challenge);
        if (requirements is not null && _options.Endpoints.RequirementsFinalize is not null)
        {
            var finalize = new JsonObject { ["prepare_token"] = requirements["prepare_token"]?.DeepClone() ?? throw new SdkException("Sentinel prepare response has no token.", "unsupported_requirements_format") };
            if (extra.TryGetValue("OpenAI-Sentinel-Proof-Token", out var proof)) finalize["proofofwork"] = proof;
            if (extra.TryGetValue("OpenAI-Sentinel-Turnstile-Token", out var turnstile)) finalize["turnstile"] = turnstile;
            using var finalizeRequest = Build(value, HttpMethod.Post, _options.Endpoints.RequirementsFinalize, finalize);
            using var finalizeResponse = await http.SendAsync(finalizeRequest, ct).ConfigureAwait(false);
            requirements = await HttpProtocol.ReadJsonAsync(finalizeResponse, ct).ConfigureAwait(false);
        }
        if (requirements?["token"]?.GetValue<string>() is { Length: > 0 } requirementsToken) extra["OpenAI-Sentinel-Chat-Requirements-Token"] = requirementsToken;
        if (_options.Endpoints.ConversationPrepare is not null)
        {
            extra["x-oai-turn-trace-id"] = Guid.NewGuid().ToString();
            var preparation = (JsonObject)body.DeepClone();
            preparation.Remove("messages");
            if (body["messages"] is JsonArray { Count: > 0 } messages)
            {
                var message = messages[^1]!;
                preparation["partial_query"] = new JsonObject { ["id"] = message["id"]?.DeepClone(), ["author"] = message["author"]?.DeepClone(), ["content"] = message["content"]?.DeepClone(), ["metadata"] = message["metadata"]?.DeepClone() };
            }
            preparation["client_prepare_state"] = "none";
            preparation["client_prepare_dispatch"] = "immediate";
            using var prepareRequest = Build(value, HttpMethod.Post, _options.Endpoints.ConversationPrepare, preparation);
            prepareRequest.Headers.Add("x-oai-turn-trace-id", extra["x-oai-turn-trace-id"]);
            using var prepareResponse = await http.SendAsync(prepareRequest, ct).ConfigureAwait(false);
            var prepared = await HttpProtocol.ReadJsonAsync(prepareResponse, ct).ConfigureAwait(false);
            if (prepared["conduit_token"]?.GetValue<string>() is { Length: > 0 } conduit) extra["x-conduit-token"] = conduit;
            body["client_prepare_state"] = "success";
            body["supported_encodings"] = new JsonArray("v1");
            extra["x-openai-web-sse-compression"] = "true";
        }
        var request = Build(value, HttpMethod.Post, _options.Endpoints.Conversation, body);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (requirements?["token"]?.GetValue<string>() is { Length: > 0 } token) request.Headers.Add("OpenAI-Sentinel-Chat-Requirements-Token", token);
        foreach (var header in extra) AddContextHeader(request, header.Key, header.Value);
        // Invoke SendAsync separately so the caller can record an uncertain send before any network write.
        return new WebTurnStream(http, request, _options.MaxEventCharacters);
    }
}

public sealed class WebTurnStream(HttpClient http, HttpRequestMessage request, int maxEventCharacters) : IDisposable
{
    public async IAsyncEnumerable<ServerSentEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new SdkException("Conversation endpoint did not return SSE. Capture the current web transport.", "unexpected_content_type");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var item in ServerSentEvents.ReadAsync(stream, maxEventCharacters, ct).ConfigureAwait(false)) yield return item;
    }
    public void Dispose() => request.Dispose();
}

public sealed record WebInputMessage(string Id, string Role, string Text)
{
    public JsonObject? Content { get; init; }
    public JsonObject? Metadata { get; init; }
    public static WebInputMessage User(string text) => new(Guid.NewGuid().ToString(), "user", text);
}

