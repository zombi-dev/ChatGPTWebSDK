using System.Net;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OpenAI;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using ChatGPTWebSdk.Mcp;

namespace ChatGPTWebSdk.Compatibility;

/// <summary>Maps observed web operations and rejects controls with no equivalent web behavior.</summary>
public sealed class OpenAiWebAdapter(ChatGptWebClient client, IReadOnlyDictionary<string, string>? modelAliases = null, WebChatContext? defaultContext = null)
{
    private McpScopeOptions? _messageMcp;
    public ChatGptWebClient Client => client;
    public OpenAiWebAdapter WithContext(string? projectId = null, bool? temporaryChat = null) =>
        new(client, modelAliases, (defaultContext ?? new()) with { ProjectId = projectId ?? defaultContext?.ProjectId, TemporaryChat = temporaryChat ?? defaultContext?.TemporaryChat ?? false }) { _messageMcp = _messageMcp };
    /// <summary>Creates a request adapter with independently registered servers for one message.</summary>
    public OpenAiWebAdapter WithMcp(McpScopeOptions? mcp) => new(client, modelAliases, defaultContext) { _messageMcp = mcp?.Snapshot() };
    private WebChatContext Context(JsonObject body)
    {
        var context = defaultContext ?? new();
        if (body["store"] is { } store)
        {
            var temporary = !store.GetValue<bool>();
            if (context.TemporaryChat && !temporary) throw new ArgumentException("store=true conflicts with the configured temporary chat context.");
            context = context with { TemporaryChat = temporary || context.TemporaryChat };
        }
        context.ResolveGizmoId();
        return context;
    }
    public async Task<WebTurnRequest> PrepareResponseAsync(ConversationScope scope, JsonObject body, CancellationToken ct = default)
    {
        CheckFields(body, ["model", "input", "stream", "conversation", "previous_response_id", "store", "reasoning", "tools"]);
        var servers = client.GetMcpServers(scope, _messageMcp);
        var mcp = SelectMcpTools(scope, body, servers);
        var context = Context(body);
        await client.ValidateContextAsync(scope, context, ct).ConfigureAwait(false);
        var conversationNode = body["conversation"];
        var conversationId = conversationNode is JsonObject obj ? obj["id"]?.GetValue<string>() : conversationNode?.GetValue<string>();
        var previous = body["previous_response_id"]?.GetValue<string>();
        if (conversationId is not null && previous is not null) throw new ArgumentException("conversation and previous_response_id cannot be combined.");
        var model = Model(body);
        var thinking = Reasoning(body["reasoning"]);
        if (scope.ThreadId.StartsWith("conv_web_", StringComparison.Ordinal) && context.TemporaryChat)
            throw new UnsupportedWebFeatureException("temporary chats within a persistent Conversations API resource; use a separate thread");
        // Validate unsupported input before linking anything or making a network request.
        var messages = await ParseResponseInputAsync(scope, body["input"], ct).ConfigureAwait(false);
        if (conversationId is not null)
        {
            var state = await client.GetStateAsync(scope, ct).ConfigureAwait(false);
            if (state.ConversationId is null) await client.LinkAsync(scope, conversationId, ct).ConfigureAwait(false);
            else if (state.ConversationId != conversationId) throw new SdkException("The requested conversation belongs to a different binding. Select another thread ID.", "conversation_binding_conflict", HttpStatusCode.Conflict);
        }
        return new() { Model = model, Messages = messages, PreviousResponseId = previous, TemporaryChat = context.TemporaryChat, ProjectId = context.ProjectId, GizmoId = context.GizmoId, ThinkingEffort = thinking,
            UseMcp = mcp?.Count != 0, McpSelections = mcp, McpServersSnapshot = servers };
    }

    public WebTurnRequest PrepareChat(JsonObject body)
    {
        CheckFields(body, ["model", "messages", "stream", "n", "stream_options", "reasoning_effort", "store"]);
        var context = Context(body);
        ValidateStreamOptions(body["stream_options"]);
        if (body["n"]?.GetValue<int>() is { } n && n != 1) throw new UnsupportedWebFeatureException("n other than 1");
        if (body["messages"] is not JsonArray array || array.Count == 0) throw new ArgumentException("messages must be a nonempty array.");
        var history = array.Select(node =>
        {
            if (node is not JsonObject m) throw new ArgumentException("Invalid chat message.");
            CheckFields(m, ["role", "content"]);
            var role = m["role"]?.GetValue<string>() ?? throw new ArgumentException("Missing message role.");
            if (role is not ("user" or "assistant")) throw new UnsupportedWebFeatureException(role + " messages");
            var text = TextContent(m["content"], "text");
            return new StoredMessage(Guid.NewGuid().ToString(), role, text);
        }).ToArray();
        var appended = history.All(m => m.Role == "user");
        return new() { Model = Model(body), Messages = appended ? history.Select(m => WebInputMessage.User(m.Text)).ToArray() : [], ExpectedHistory = appended ? null : history,
            ThinkingEffort = ReasoningEffort(body["reasoning_effort"]?.GetValue<string>()), TemporaryChat = context.TemporaryChat, ProjectId = context.ProjectId, GizmoId = context.GizmoId, Mcp = _messageMcp };
    }

    public async Task<WebTurnRequest> PrepareChatAsync(ConversationScope scope, JsonObject body, CancellationToken ct = default)
    {
        CheckFields(body, ["model", "messages", "stream", "n", "stream_options", "reasoning_effort", "store"]);
        var context = Context(body);
        await client.ValidateContextAsync(scope, context, ct).ConfigureAwait(false);
        ValidateStreamOptions(body["stream_options"]);
        if (body["n"]?.GetValue<int>() is { } n && n != 1) throw new UnsupportedWebFeatureException("n other than 1");
        var model = Model(body);
        var thinking = ReasoningEffort(body["reasoning_effort"]?.GetValue<string>());
        if (body["messages"] is not JsonArray { Count: > 0 } array) throw new ArgumentException("messages must be a nonempty array.");
        var messages = new List<StoredMessage>();
        foreach (var node in array)
        {
            var message = node as JsonObject ?? throw new ArgumentException("Invalid chat message.");
            CheckFields(message, ["role", "content"]);
            var role = message["role"]?.GetValue<string>() ?? throw new ArgumentException("Missing role.");
            if (role is not ("user" or "assistant")) throw new UnsupportedWebFeatureException(role + " messages");
            var input = await ParseMessageAsync(scope, message["content"], "text", role, ct).ConfigureAwait(false);
            messages.Add(new(input.Id, role, input.Text) { Content = input.Content, Metadata = input.Metadata });
        }
        bool append = messages.All(m => m.Role == "user");
        return new() { Model = model, Messages = append ? messages.Select(m => new WebInputMessage(m.Id, m.Role, m.Text) { Content = m.Content, Metadata = m.Metadata }).ToArray() : [], ExpectedHistory = append ? null : messages,
            ThinkingEffort = thinking, TemporaryChat = context.TemporaryChat, ProjectId = context.ProjectId, GizmoId = context.GizmoId,
            McpServersSnapshot = client.GetMcpServers(scope, _messageMcp) };
    }

    private IReadOnlyList<McpServerSelection>? SelectMcpTools(ConversationScope scope, JsonObject body, IReadOnlyList<McpServerConfiguration> servers)
    {
        if (body["tools"] is null) return null;
        if (body["tools"] is not JsonArray array) throw new ArgumentException("tools must be an array.");
        var result = new List<McpServerSelection>();
        foreach (var node in array)
        {
            if (node is not JsonObject tool || tool["type"]?.GetValue<string>() != "mcp") throw new UnsupportedWebFeatureException("non-MCP response tools");
            CheckFields(tool, ["type", "server_label", "server_url", "server_description", "allowed_tools", "require_approval", "headers", "authorization"]);
            var label = tool["server_label"]?.GetValue<string>() ?? throw new ArgumentException("MCP server_label is required.");
            var configured = servers.SingleOrDefault(s => s.Label == label)
                ?? throw new SdkException("Register this MCP server at initialization, for this chat, or for this message before requesting its tools.", "mcp_server_not_registered", HttpStatusCode.Forbidden);
            if (client.Mcp?.IsServerAllowed?.Invoke(scope, label) == false) throw new SdkException("This application user cannot access this MCP server.", "mcp_server_denied", HttpStatusCode.Forbidden);
            if (result.Any(s => s.Server.Label == label)) throw new ArgumentException("Duplicate MCP server label.");
            if (tool["server_url"] is { } url && (!Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var endpoint) || endpoint != configured.Endpoint))
                throw new SdkException("The requested MCP endpoint does not match the registered server.", "mcp_server_denied", HttpStatusCode.Forbidden);
            IReadOnlyList<string>? allowed = null; bool? readOnly = null;
            if (tool["allowed_tools"] is JsonArray names) allowed = names.Select(n => n!.GetValue<string>()).ToArray();
            else if (tool["allowed_tools"] is JsonObject filter)
            {
                CheckFields(filter, ["tool_names", "read_only"]);
                if (filter["tool_names"] is JsonArray filtered) allowed = filtered.Select(n => n!.GetValue<string>()).ToArray();
                readOnly = filter["read_only"]?.GetValue<bool>();
            }
            else if (tool["allowed_tools"] is not null) throw new ArgumentException("Invalid MCP allowed_tools filter.");
            var approval = tool["require_approval"]?.DeepClone() ?? JsonValue.Create("always")!;
            if (approval is JsonValue scalar && scalar.GetValue<string>() is not ("always" or "never")) throw new ArgumentException("Invalid MCP approval setting.");
            if (approval is JsonObject approvalFilter)
            {
                CheckFields(approvalFilter, ["always", "never"]);
                if (approvalFilter.Count != 1) throw new ArgumentException("Select one MCP approval filter.");
                var value = approvalFilter.First().Value as JsonObject ?? throw new ArgumentException("Invalid MCP approval filter.");
                CheckFields(value, ["tool_names", "read_only"]);
                if (value["tool_names"] is { } list && list is not JsonArray) throw new ArgumentException("MCP tool_names must be an array.");
                if (value["tool_names"] is JsonArray values) foreach (var name in values) _ = name!.GetValue<string>();
                if (value["read_only"] is { } ro) _ = ro.GetValue<bool>();
            }
            else if (approval is not JsonValue) throw new ArgumentException("Invalid MCP approval setting.");
            var headers = configured.Headers.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (tool["headers"] is JsonObject supplied) foreach (var pair in supplied) headers[pair.Key] = pair.Value!.GetValue<string>();
            else if (tool["headers"] is not null) throw new ArgumentException("MCP headers must be an object.");
            if (tool["authorization"] is { } authorization) headers["Authorization"] = "Bearer " + authorization.GetValue<string>();
            if ((tool["headers"] is not null || tool["authorization"] is not null) && configured.Endpoint is null) throw new ArgumentException("HTTP headers require a remote MCP server.");
            var selected = configured with { Headers = headers };
            selected.Validate();
            result.Add(new(selected, allowed, readOnly, approval));
        }
        return result;
    }

    private static void ValidateStreamOptions(JsonNode? options)
    {
        if (options is not JsonObject obj) return;
        CheckFields(obj, ["include_usage"]);
        // ChatGPT does not return billable API token counts. Usage stays absent, including in streaming mode.
    }
    private static string? Reasoning(JsonNode? reasoning)
    {
        if (reasoning is not JsonObject obj) return null;
        CheckFields(obj, ["effort"]);
        return ReasoningEffort(obj["effort"]?.GetValue<string>());
    }
    private static string? ReasoningEffort(string? effort) => effort switch { null => null, "high" => "extended", _ => throw new UnsupportedWebFeatureException("reasoning effort " + effort) };

    private string Model(JsonObject body)
    {
        var model = body["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("model is required; select an available ChatGPT web model slug.");
        return modelAliases?.TryGetValue(model, out var slug) == true ? slug : model;
    }
    private async Task<IReadOnlyList<WebInputMessage>> ParseResponseInputAsync(ConversationScope scope, JsonNode? input, CancellationToken ct)
    {
        if (input is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return [WebInputMessage.User(text)];
        if (input is not JsonArray array || array.Count == 0) throw new ArgumentException("input must be a string or nonempty message array.");
        var result = new List<WebInputMessage>();
        foreach (var node in array)
        {
            if (node is not JsonObject message) throw new UnsupportedWebFeatureException("non-message response input");
            CheckFields(message, ["role", "content", "type"]);
            if (message["type"]?.GetValue<string>() is { } type && type != "message") throw new UnsupportedWebFeatureException(type);
            if (message["role"]?.GetValue<string>() != "user") throw new UnsupportedWebFeatureException("non-user response input");
            result.Add(await ParseMessageAsync(scope, message["content"], "input_text", "user", ct).ConfigureAwait(false));
        }
        return result;
    }

    private async Task<WebInputMessage> ParseMessageAsync(ConversationScope scope, JsonNode? content, string textType, string role, CancellationToken ct)
    {
        if (content is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return new(Guid.NewGuid().ToString(), role, text);
        if (content is not JsonArray { Count: > 0 } parts) throw new ArgumentException("Message content must be text or a nonempty array.");
        var textParts = new List<string>(); var images = new List<WebUploadedFile>();
        // Reject unknown part kinds before any upload is sent.
        foreach (var node in parts)
        {
            var part = node as JsonObject ?? throw new ArgumentException("Invalid content part.");
            var type = part["type"]?.GetValue<string>();
            if (type == textType) { CheckFields(part, ["type", "text"]); textParts.Add(part["text"]?.GetValue<string>() ?? throw new ArgumentException("Missing text.")); }
            else if (role == "user" && type is "image_url" or "input_image") CheckFields(part, ["type", "image_url", "detail", "file_id"]);
            else throw new UnsupportedWebFeatureException("content part " + type);
        }
        foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() is "image_url" or "input_image"))
        {
            string? id = part["file_id"]?.GetValue<string>();
            if (id is not null) images.Add(await GetOwnedFileAsync(scope, id, ct).ConfigureAwait(false));
            else
            {
                var imageUrl = part["image_url"];
                string? url = imageUrl is JsonObject obj ? obj["url"]?.GetValue<string>() : imageUrl?.GetValue<string>();
                if (imageUrl is JsonObject details) { CheckFields(details, ["url", "detail"]); if (details["detail"]?.GetValue<string>() is not (null or "auto")) throw new UnsupportedWebFeatureException("image detail override"); }
                if (part["detail"]?.GetValue<string>() is not (null or "auto")) throw new UnsupportedWebFeatureException("image detail override");
                if (url is null || !url.StartsWith("data:image/", StringComparison.Ordinal) || !url.Contains(";base64,", StringComparison.Ordinal))
                    throw new UnsupportedWebFeatureException("external image URLs; use binary image data or an uploaded file ID");
                var comma = url.IndexOf(','); var mime = url[5..url.IndexOf(';')];
                if (url.Length - comma > 32 * 1024 * 1024) throw new ArgumentException("Image input is too large.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(url[(comma + 1)..]); } catch (FormatException) { throw new ArgumentException("Invalid base64 image input."); }
                var dimensions = ImageDimensions.Read(bytes, mime);
                var name = mime == "image/png" ? "image.png" : "image.jpg";
                images.Add(await UploadOwnedFileAsync(scope, new MemoryStream(bytes, writable: false), new() { FileName = name, MimeType = mime, Width = dimensions.Width, Height = dimensions.Height }, ct).ConfigureAwait(false));
            }
        }
        var joined = string.Concat(textParts);
        if (images.Count == 0) return new(Guid.NewGuid().ToString(), role, joined);
        var nativeParts = new JsonArray(); var attachments = new JsonArray();
        foreach (var image in images)
        {
            var message = image.ToMessage("");
            nativeParts.Add(message.Content!["parts"]![0]!.DeepClone());
            attachments.Add(message.Metadata!["attachments"]![0]!.DeepClone());
        }
        if (joined.Length > 0) nativeParts.Add(joined);
        return WebInputMessage.User(joined) with { Content = new() { ["content_type"] = "multimodal_text", ["parts"] = nativeParts }, Metadata = new() { ["attachments"] = attachments } };
    }

    public async Task<WebUploadedFile> UploadOwnedFileAsync(ConversationScope scope, Stream input, WebFileUploadOptions options, CancellationToken ct = default)
    {
        await using var lease = await client.Store.AcquireAsync(new(scope.AccountId, scope.UserId, "__sdk_files"), ct).ConfigureAwait(false);
        // Image data is hashed for history resubmissions so the same attachment isn't uploaded on every turn.
        using var data = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { if (data.Length + count > options.MaxBytes) throw new ArgumentException("Upload exceeds MaxBytes."); await data.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.ToArray()));
        var cached = lease.State.Files.Values.FirstOrDefault(f => f.Sha256 == hash && f.MimeType == options.MimeType && f.Purpose == options.Purpose && f.Name == options.FileName);
        if (cached is not null) return cached;
        data.Position = 0;
        var file = await client.Transport.UploadFileAsync(scope.AccountId, data, options, ct).ConfigureAwait(false) with { Sha256 = hash };
        lease.State.Files[file.Id] = file;
        await lease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        return file;
    }
    public async Task<WebUploadedFile> GetOwnedFileAsync(ConversationScope scope, string id, CancellationToken ct = default)
    {
        await using var lease = await client.Store.AcquireAsync(new(scope.AccountId, scope.UserId, "__sdk_files"), ct).ConfigureAwait(false);
        return lease.State.Files.TryGetValue(id, out var file) ? file : throw new SdkException("File is not owned by this application user.", "file_not_found", HttpStatusCode.NotFound);
    }
    private static string TextContent(JsonNode? content, string type)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is not JsonArray array || array.Count == 0) throw new UnsupportedWebFeatureException("empty or non-text message content");
        return string.Concat(array.Select(node =>
        {
            if (node is not JsonObject part || part["type"]?.GetValue<string>() != type) throw new UnsupportedWebFeatureException("multimodal message content");
            CheckFields(part, ["type", "text"]);
            return part["text"]?.GetValue<string>() ?? throw new ArgumentException("Missing text.");
        }));
    }
    private static void CheckFields(JsonObject body, string[] fields)
    {
        foreach (var item in body)
            if (item.Value is not null && !fields.Contains(item.Key)) throw new UnsupportedWebFeatureException(item.Key);
    }

    public static JsonObject ResponseJson(WebResponseRecord response, string status = "completed") => new()
    {
        ["id"] = response.Id, ["object"] = "response", ["created_at"] = response.CreatedAt,
        ["status"] = status, ["model"] = response.Model, ["error"] = null, ["incomplete_details"] = null,
        ["instructions"] = null, ["max_output_tokens"] = null, ["previous_response_id"] = response.PreviousResponseId,
        ["conversation"] = new JsonObject { ["id"] = response.ConversationId },
        ["output"] = new JsonArray(OutputMessage(response)),
        ["parallel_tool_calls"] = false, ["tool_choice"] = "none", ["tools"] = new JsonArray(),
        ["temperature"] = null, ["top_p"] = null, ["usage"] = null, ["metadata"] = new JsonObject(),
        ["chatgpt_web"] = new JsonObject { ["conversation_id"] = response.ConversationId, ["message_id"] = response.MessageId, ["usage_available"] = false }
    };
    public static JsonObject OutputMessage(WebResponseRecord response) => new()
    {
        ["id"] = response.MessageId, ["type"] = "message", ["role"] = "assistant", ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = response.Text, ["annotations"] = new JsonArray() })
    };
    public static JsonObject ChatJson(WebResponseRecord response) => new()
    {
        ["id"] = "chatcmpl_" + response.Id, ["object"] = "chat.completion", ["created"] = response.CreatedAt, ["model"] = response.Model,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = response.Text }, ["finish_reason"] = "stop" })
    };
    public async Task<JsonObject> ListModelsAsync(ConversationScope scope, CancellationToken ct = default)
    {
        var raw = await client.Transport.GetModelsAsync(scope.AccountId, ct).ConfigureAwait(false);
        if (raw["models"] is not JsonArray models) throw new SdkException("Unknown web models response. Provide a current capture.", "unsupported_models_format");
        return new() { ["object"] = "list", ["data"] = new JsonArray(models.Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m!["slug"]?.GetValue<string>() ?? throw new SdkException("Web model has no slug.", "unsupported_models_format"),
            ["object"] = "model", ["created"] = 0, ["owned_by"] = "chatgpt-web"
        }).ToArray()) };
    }
}
