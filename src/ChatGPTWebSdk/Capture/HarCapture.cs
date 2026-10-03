using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Capture;

public sealed record CaptureEntry(string Method, string Path, int Status, string? ContentType, bool HasAuthorization, bool HasCookies, bool HasResponseBody);
public sealed record CaptureReport(IReadOnlyList<CaptureEntry> Entries, bool HasFirstTurn, bool HasContinuation, bool HasRequirements, bool HasSession)
{
    public string[] RequiredChallenges { get; init; } = [];
    public bool HasConversationPrepare { get; init; }
    public bool HasFiles { get; init; }
    public bool HasTemporaryChats { get; init; }
    public bool HasProjectChats { get; init; }
    public bool HasRegeneration { get; init; }
}
public sealed class HarImport
{
    public required WebCredentials Credentials { get; init; }
    public required WebEndpointProfile Endpoints { get; init; }
    public required CaptureReport Report { get; init; }
    public override string ToString() => "[HarImport credentials redacted]";
}

/// <summary>Reads captures locally. Never replays captured turns or imports transient challenge tokens.</summary>
public static class HarCapture
{
    public static async Task<HarImport> ImportAsync(Stream stream, CancellationToken ct = default)
    {
        var har = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var entries = har?["log"]?["entries"] as JsonArray ?? throw new InvalidDataException("Not a HAR with log.entries.");
        string? accessToken = null, cookie = null;
        DateTimeOffset? expiresAt = null;
        string userAgent = "ChatGPTWebSdk/0.1";
        var contextHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<CaptureEntry>();
        var profile = new WebEndpointProfile();
        string sessionPath = profile.Session, turnPath = profile.Conversation, requirementsPath = profile.Requirements, modelsPath = profile.Models, accountsPath = profile.Accounts;
        string? requirementsPrepare = null, requirementsFinalize = null, conversationPrepare = null;
        var challenges = new HashSet<string>();
        bool files = false, temporary = false, regeneration = false, projects = false;
        bool first = false, continuation = false, requirements = false, session = false;
        JsonObject defaults = new(), requirementsBody = new();
        foreach (var entry in entries)
        {
            var request = entry?["request"];
            if (!Uri.TryCreate(request?["url"]?.GetValue<string>(), UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host is not ("chatgpt.com" or "chat.openai.com") || !uri.IsDefaultPort) continue;
            var path = uri.AbsolutePath;
            if (!(path.StartsWith("/backend-api/", StringComparison.Ordinal) || path == "/api/auth/session")) continue;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request?["headers"] is JsonArray headerArray)
                foreach (var header in headerArray)
                    if (header?["name"]?.GetValue<string>() is { } name && header["value"]?.GetValue<string>() is { } value) headers[name] = value;
            if (headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) accessToken = auth[7..];
            if (headers.TryGetValue("Cookie", out var cookieHeader)) cookie = cookieHeader;
            else if (request?["cookies"] is JsonArray cookies && cookies.Count > 0)
                cookie = string.Join("; ", cookies.Select(c => c!["name"]!.GetValue<string>() + "=" + c["value"]!.GetValue<string>()));
            if (headers.TryGetValue("User-Agent", out var agent)) userAgent = agent;
            foreach (var pair in headers)
                if ((pair.Key.StartsWith("oai-", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("sec-ch-ua", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.Equals("accept-language", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("OpenAI-Organization", StringComparison.OrdinalIgnoreCase) ||
                     pair.Key.Equals("ChatGPT-Account-Id", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("x-oai-mcp-form-version", StringComparison.OrdinalIgnoreCase)) &&
                    !pair.Key.Contains("Sentinel", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("OAI-Echo-Logs", StringComparison.OrdinalIgnoreCase)) contextHeaders[pair.Key] = pair.Value;
            var response = entry?["response"];
            var content = response?["content"];
            var responseText = content?["text"]?.GetValue<string>();
            if (content?["encoding"]?.GetValue<string>() == "base64" && responseText is not null)
            {
                try { responseText = Encoding.UTF8.GetString(Convert.FromBase64String(responseText)); }
                catch (FormatException) { responseText = null; }
            }
            var method = request?["method"]?.GetValue<string>() ?? "";
            summaries.Add(new(method, path, response?["status"]?.GetValue<int>() ?? 0, content?["mimeType"]?.GetValue<string>(),
                headers.ContainsKey("Authorization"), headers.ContainsKey("Cookie") || request?["cookies"] is JsonArray { Count: > 0 }, !string.IsNullOrEmpty(responseText)));
            JsonObject? body = null;
            if (request?["postData"]?["text"]?.GetValue<string>() is { } text)
            {
                try { body = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
            }
            if (path == "/api/auth/session")
            {
                session = true; sessionPath = path.TrimStart('/');
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    try
                    {
                        var sessionBody = JsonNode.Parse(responseText);
                        accessToken = sessionBody?["accessToken"]?.GetValue<string>() ?? accessToken;
                        if (DateTimeOffset.TryParse(sessionBody?["expires"]?.GetValue<string>(), out var parsedExpiry)) expiresAt = parsedExpiry;
                    }
                    catch (JsonException) { }
                }
            }
            if (path.EndsWith("/models", StringComparison.Ordinal) && method == "GET") modelsPath = path.TrimStart('/');
            if (path.StartsWith("/backend-api/accounts/check", StringComparison.Ordinal)) accountsPath = path.TrimStart('/');
            if (path == "/backend-api/f/conversation/prepare") conversationPrepare = path.TrimStart('/');
            if (path.EndsWith("/chat-requirements/prepare", StringComparison.Ordinal)) { requirements = true; requirementsPrepare = path.TrimStart('/'); }
            if (path.EndsWith("/chat-requirements/finalize", StringComparison.Ordinal)) requirementsFinalize = path.TrimStart('/');
            if (path.Contains("/chat-requirements", StringComparison.Ordinal) && responseText is not null)
            {
                try
                {
                    var requirementResponse = JsonNode.Parse(responseText);
                    foreach (var name in new[] { "turnstile", "proofofwork", "arkose", "so" })
                        if (requirementResponse?[name]?["required"]?.GetValue<bool>() == true) challenges.Add(name);
                }
                catch (JsonException) { }
            }
            files |= path == "/backend-api/files" && method == "POST";
            if (path.EndsWith("/chat-requirements", StringComparison.Ordinal))
            {
                requirements = true; requirementsPath = path.TrimStart('/');
                // 'p' and browser proofs can be transient. Require a provider for each request.
                requirementsBody = new();
            }
            if (path.EndsWith("/conversation", StringComparison.Ordinal) && method == "POST" && body is not null)
            {
                turnPath = path.TrimStart('/');
                if (body["conversation_id"] is null) first = true; else continuation = true;
                temporary |= body["history_and_training_disabled"]?.GetValue<bool>() == true;
                projects |= body["gizmo_id"]?.GetValue<string>()?.StartsWith("g-p-", StringComparison.Ordinal) == true;
                regeneration |= body["action"]?.GetValue<string>() == "variant";
                // Copy only protocol defaults, never prompts, IDs, attachments, tools or proof material.
                foreach (var name in new[] { "timezone_offset_min", "timezone", "supports_buffering", "supported_encodings" })
                    if (body[name] is { } value) defaults[name] = value.DeepClone();
            }
        }
        return new()
        {
            Credentials = new() { AccessToken = accessToken, CookieHeader = cookie, ExpiresAt = expiresAt, UserAgent = userAgent, Headers = contextHeaders },
            Endpoints = new() { Session = sessionPath, Conversation = turnPath, Requirements = requirementsPath, Models = modelsPath, Accounts = accountsPath, ConversationDefaults = defaults, RequirementsBody = requirementsBody,
                RequirementsPrepare = requirementsPrepare, RequirementsFinalize = requirementsFinalize, ConversationPrepare = conversationPrepare },
            Report = new(summaries, first, continuation, requirements, session) { RequiredChallenges = challenges.Order().ToArray(), HasConversationPrepare = conversationPrepare is not null, HasFiles = files, HasTemporaryChats = temporary, HasProjectChats = projects, HasRegeneration = regeneration }
        };
    }

    public static async Task<WebCredentials> ImportCookiesAsync(Stream stream, string? accessToken = null, CancellationToken ct = default)
    {
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var array = node as JsonArray ?? node?["cookies"] as JsonArray ?? throw new InvalidDataException("Expected a cookie array or an object containing cookies.");
        var cookies = array.Where(c => c?["domain"]?.GetValue<string>()?.TrimStart('.') is "chatgpt.com" or "chat.openai.com" or "openai.com")
            .Where(c => c!["name"]?.GetValue<string>() is not null && c["value"]?.GetValue<string>() is not null)
            .Select(c => c!["name"]!.GetValue<string>() + "=" + c["value"]!.GetValue<string>()).ToArray();
        if (cookies.Length == 0) throw new SdkException("No ChatGPT cookies were found.", "authentication_required");
        return new() { AccessToken = accessToken, CookieHeader = string.Join("; ", cookies) };
    }
}

