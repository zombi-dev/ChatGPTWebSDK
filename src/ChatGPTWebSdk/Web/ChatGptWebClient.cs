using System.Net;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Mcp;

namespace ChatGPTWebSdk.Web;

public enum WebTurnAction { Append, Edit, Regenerate }

public sealed class WebTurnRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<WebInputMessage> Messages { get; init; }
    public string? PreviousResponseId { get; init; }
    // Optional complete transcript, for clients that send the Chat Completions history on every call.
    public IReadOnlyList<StoredMessage>? ExpectedHistory { get; init; }
    public WebTurnAction Action { get; init; }
    public string? EditMessageId { get; init; }
    public bool TemporaryChat { get; init; }
    public string? ThinkingEffort { get; init; }
    public string? GizmoId { get; init; }
    public string? ProjectId { get; init; }
    public bool UseMcp { get; init; } = true;
    /// <summary>Servers for this logical message only. They remain selected through all of its tool rounds.</summary>
    public McpScopeOptions? Mcp { get; init; }
    internal IReadOnlyList<McpServerSelection>? McpSelections { get; init; }
    internal IReadOnlyList<McpServerConfiguration>? McpServersSnapshot { get; init; }
    internal bool InternalMcpTurn { get; init; }
}

public sealed record WebChatResult(WebResponseRecord Response)
{
    public string Text => Response.Text;
    public Uri ConversationUrl => new("https://chatgpt.com/c/" + Uri.EscapeDataString(Response.ConversationId));
}
public sealed record WebChatEvent(WebStreamUpdate Update, WebResponseRecord? Response = null)
{
    public required string ResponseId { get; init; }
    public required long CreatedAt { get; init; }
}

public sealed class ChatGptWebClient(ChatGptWebTransport transport, IConversationStore store)
{
    private readonly McpConversationBridge? _mcp;
    private readonly ConcurrentDictionary<ConversationScope, McpScopeOptions> _chatMcp = new();
    private readonly McpConversationOptions _defaultMcp = new();
    private readonly IReadOnlyList<McpServerConfiguration> _initialServers = [];
    public McpConversationOptions? Mcp => _mcp?.Options;
    public ChatGptWebClient(ChatGptWebTransport transport, IConversationStore store, McpConversationOptions mcp) : this(transport, store)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        mcp.Validate();
        _initialServers = McpScopeOptions.SnapshotServers(mcp.Servers);
        _defaultMcp = mcp;
        _mcp = new(this, mcp);
    }
    /// <summary>Configures MCP servers for this account/user/thread. Null restores initialization defaults.</summary>
    public void SetChatMcp(ConversationScope scope, McpScopeOptions? mcp)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (mcp is null) _chatMcp.TryRemove(scope, out _);
        else _chatMcp[scope] = mcp.Snapshot();
    }
    /// <summary>Returns this chat's configuration, or null if it inherits initialization defaults.</summary>
    public McpScopeOptions? GetChatMcp(ConversationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _chatMcp.TryGetValue(scope, out var configured) ? configured.Snapshot() : null;
    }
    internal IReadOnlyList<McpServerConfiguration> GetMcpServers(ConversationScope scope, McpScopeOptions? message = null)
    {
        var servers = _initialServers.ToDictionary(s => s.Label, StringComparer.Ordinal);
        if (_chatMcp.TryGetValue(scope, out var chat)) chat.Apply(servers);
        message?.Snapshot().Apply(servers);
        return servers.Values.ToArray();
    }
    public ChatGptWebTransport Transport => transport;
    public IConversationStore Store => store;
    public async Task<WebChatResult> EditAsync(ConversationScope scope, string messageId, string text, string model, CancellationToken ct = default) =>
        await SendAsync(scope, new WebTurnRequest { Model = model, Messages = [WebInputMessage.User(text)], Action = WebTurnAction.Edit, EditMessageId = messageId,
            TemporaryChat = (await GetStateAsync(scope, ct).ConfigureAwait(false)).TemporaryChat == true }, ct).ConfigureAwait(false);
    public async Task<WebChatResult> RegenerateAsync(ConversationScope scope, string model, CancellationToken ct = default) =>
        await SendAsync(scope, new WebTurnRequest { Model = model, Messages = [], Action = WebTurnAction.Regenerate,
            TemporaryChat = (await GetStateAsync(scope, ct).ConfigureAwait(false)).TemporaryChat == true }, ct).ConfigureAwait(false);
    private readonly InMemoryConversationStore _temporary = new();

    private async ValueTask<IConversationLease> LeaseAsync(ConversationScope scope, bool? temporary, CancellationToken ct)
    {
        var lease = await store.AcquireAsync(scope, ct).ConfigureAwait(false);
        if (temporary is not null && lease.State.TemporaryChat is { } mode && mode != temporary)
        { await lease.DisposeAsync().ConfigureAwait(false); throw new SdkException("Temporary and persistent chats require different thread IDs.", "conversation_mode_conflict", HttpStatusCode.Conflict); }
        if (temporary == true && lease.State.TemporaryChat is null)
        {
            if (lease.State.ConversationId is not null || lease.State.History.Count > 0)
            { await lease.DisposeAsync().ConfigureAwait(false); throw new SdkException("Select a new thread ID for a temporary chat.", "conversation_mode_conflict", HttpStatusCode.Conflict); }
            lease.State.TemporaryChat = true;
            await lease.SaveAsync(ct).ConfigureAwait(false);
        }
        if (lease.State.TemporaryChat == true)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            var temporaryLease = await _temporary.AcquireAsync(scope, ct).ConfigureAwait(false);
            temporaryLease.State.TemporaryChat = true;
            return temporaryLease;
        }
        return lease;
    }

    public async Task<WebChatResult> SendAsync(ConversationScope scope, string text, string model, CancellationToken ct = default) =>
        await SendAsync(scope, new WebTurnRequest { Model = model, Messages = [WebInputMessage.User(text)] }, ct).ConfigureAwait(false);

    public async Task<WebChatResult> SendAsync(ConversationScope scope, WebTurnRequest request, CancellationToken ct = default)
    {
        WebResponseRecord? result = null;
        await foreach (var item in StreamAsync(scope, request, ct).ConfigureAwait(false)) result = item.Response ?? result;
        return new(result ?? throw new SdkException("No confirmed response was returned.", "incomplete_web_response"));
    }

    public async IAsyncEnumerable<WebChatEvent> StreamAsync(ConversationScope scope, WebTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One lease spans the entire tool loop, including the time between remote generation turns.
        transport.ModelPolicy.Resolve(request.Model);
        await using var operation = await store.AcquireAsync(new(scope.AccountId, scope.UserId, "__sdk_operation_" + scope.StorageKey), ct).ConfigureAwait(false);
        var events = request.UseMcp ? (_mcp ?? new McpConversationBridge(this, _defaultMcp)).StreamAsync(scope, request, ct) : StreamCoreAsync(scope, request, ct);
        await foreach (var item in events.ConfigureAwait(false)) yield return item;
    }

    internal ValueTask<IConversationLease> AcquireMcpStateAsync(ConversationScope scope, CancellationToken ct, bool? temporary = null) => LeaseAsync(scope, temporary, ct);

    /// <summary>Returns calls whose outcome is unknown. Never retry these calls without independent confirmation.</summary>
    public async Task<IReadOnlyList<McpToolExecution>> GetPendingMcpToolCallsAsync(ConversationScope scope, CancellationToken ct = default) =>
        (await GetStateAsync(scope, ct).ConfigureAwait(false)).McpExecutions.Where(c => c.Status is "executing" or "uncertain").ToArray();

    /// <summary>Records an independently confirmed result without executing the tool again. The next MCP turn appends it.</summary>
    public async Task ResolveMcpToolCallAsync(ConversationScope scope, string callId, JsonObject confirmedResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedResult);
        if (confirmedResult.ToJsonString().Length > (Mcp?.MaxToolResultCharacters ?? 512 * 1024)) throw new ArgumentException("MCP result exceeds its configured limit.");
        await using var operation = await store.AcquireAsync(new(scope.AccountId, scope.UserId, "__sdk_operation_" + scope.StorageKey), ct).ConfigureAwait(false);
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        var call = lease.State.McpExecutions.SingleOrDefault(c => c.Id == callId) ?? throw new ArgumentException("Unknown MCP call ID.");
        if (call.Status is not ("executing" or "uncertain")) throw new ArgumentException("This MCP call already has a confirmed result.");
        call.Result = (JsonObject)confirmedResult.DeepClone(); call.Status = "completed";
        await lease.SaveAsync(ct).ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<WebChatEvent> StreamCoreAsync(ConversationScope scope, WebTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        transport.ModelPolicy.Resolve(request.Model);
        var requestedGizmo = new WebChatContext { ProjectId = request.ProjectId, GizmoId = request.GizmoId, TemporaryChat = request.TemporaryChat }.ResolveGizmoId();
        await using var lease = await LeaseAsync(scope, request.TemporaryChat, ct).ConfigureAwait(false);
        var state = lease.State;
        ValidateContext(state, requestedGizmo, request.TemporaryChat);
        state.TemporaryChat = request.TemporaryChat;
        if (state.RequiresReconciliation) throw new ConversationReconciliationException();
        if (!request.InternalMcpTurn && state.McpExecutions.Any(c => c.Status is "executing" or "uncertain"))
            throw new SdkException("An MCP call has an unknown outcome. Confirm it with ResolveMcpToolCallAsync before continuing.", "mcp_tool_outcome_unknown", HttpStatusCode.Conflict);
        if (request.PreviousResponseId is not null && request.PreviousResponseId != state.LastResponseId)
            throw new SdkException("previous_response_id must be the latest response in this account/user/thread. Use a separate linked thread to branch.", "response_lineage_conflict", HttpStatusCode.Conflict);
        IReadOnlyList<WebInputMessage> messages = request.Messages;
        if (request.ExpectedHistory is not null)
        {
            var expected = request.ExpectedHistory;
            var history = state.VisibleHistory ?? state.History;
            if (expected.Count < history.Count || history.Where((m, i) => m.Role != expected[i].Role || m.Text != expected[i].Text || !JsonNode.DeepEquals(m.Content, expected[i].Content)).Any())
                throw new SdkException("The supplied history does not match the linked conversation. Choose a new thread or send only the appended input through Responses.", "history_mismatch", HttpStatusCode.Conflict);
            var tail = expected.Skip(history.Count).ToArray();
            if (tail.Length == 0 || tail.Any(m => m.Role != "user")) throw new UnsupportedWebFeatureException("history tail containing assistant, system, developer or tool messages");
            messages = tail.Select(m => WebInputMessage.User(m.Text) with { Content = m.Content, Metadata = m.Metadata }).ToArray();
        }
        int? truncateAt = null;
        var parent = state.ParentMessageId;
        if (request.Action is WebTurnAction.Edit or WebTurnAction.Regenerate)
        {
            if (state.ConversationId is null || request.ExpectedHistory is not null) throw new ArgumentException("Editing or regenerating requires a linked conversation and native input.");
            var index = request.Action == WebTurnAction.Edit ? state.History.FindIndex(m => m.Id == request.EditMessageId && m.Role == "user") : state.History.FindLastIndex(m => m.Role == "user");
            if (index < 0) throw new ArgumentException("The selected user message is not on this conversation's current branch.");
            var remote = await transport.GetConversationAsync(scope.AccountId, state.ConversationId, ct).ConfigureAwait(false);
            var node = remote["mapping"]?.AsObject().FirstOrDefault(n => n.Value?["message"]?["id"]?.GetValue<string>() == state.History[index].Id).Value
                ?? throw new SdkException("Selected message is missing from the remote conversation.", "remote_message_not_found");
            parent = request.Action == WebTurnAction.Edit ? node["parent"]?.GetValue<string>() ?? "client-created-root" : state.History[index].Id;
            truncateAt = request.Action == WebTurnAction.Edit ? index : index + 1;
            if (request.Action == WebTurnAction.Regenerate) messages = [];
        }
        var body = transport.CreateTurnBody(request.Model, parent, state.ConversationId,
            messages.Count == 0 && request.Action == WebTurnAction.Regenerate ? [WebInputMessage.User("")] : messages);
        if (request.Action == WebTurnAction.Regenerate)
        { body["action"] = "variant"; body["messages"] = new JsonArray(); body["enable_message_followups"] = true; }
        if (request.TemporaryChat)
        {
            body["history_and_training_disabled"] = true;
            // The captured protocol includes this creation-only field on the first temporary turn.
            if (state.ConversationId is null) body["temporary_chat_requests_personalization"] = false;
            else body.Remove("temporary_chat_requests_personalization");
        }
        if (request.ThinkingEffort is not null) body["thinking_effort"] = request.ThinkingEffort;
        var gizmo = requestedGizmo ?? state.GizmoId;
        if (gizmo is not null) { body["gizmo_id"] = gizmo; body["conversation_mode"] = new JsonObject { ["kind"] = "gizmo_interaction", ["gizmo_id"] = gizmo }; }
        using var turn = await transport.OpenTurnAsync(scope.AccountId, body, ct).ConfigureAwait(false);
        state.GizmoId = gizmo;
        state.RequiresReconciliation = true;
        state.PendingMessages = messages.ToList();
        state.PreviousAssistantBeforePendingTurn = request.Action == WebTurnAction.Regenerate ? state.History.LastOrDefault(m => m.Role == "assistant")?.Id : null;
        await lease.SaveAsync(ct).ConfigureAwait(false);
        var decoder = new WebStreamDecoder();
        var responseId = "resp_web_" + Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        WebStreamUpdate? last = null;
        bool saved = false;
        try
        {
            await using var events = turn.ReadAsync(ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool hasEvent;
                try { hasEvent = await events.MoveNextAsync().ConfigureAwait(false); }
                catch (SdkException ex) when (last is null && ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests)
                {
                    // A definite HTTP rejection before any SSE data is safe to retry after correcting the request.
                    state.RequiresReconciliation = false;
                    state.PendingMessages.Clear();
                    await lease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                    saved = true;
                    throw;
                }
                if (!hasEvent) break;
                var item = events.Current;
                var update = decoder.Decode(item);
                if (update.ConversationId is not null && update.ConversationId != state.ConversationId)
                {
                    if (state.ConversationId is not null) throw new SdkException("The backend returned a different conversation ID.", "conversation_mismatch");
                    await (request.TemporaryChat ? _temporary : store).ClaimRemoteConversationAsync(scope, update.ConversationId, CancellationToken.None).ConfigureAwait(false);
                    state.ConversationId = update.ConversationId;
                    // Persist the remote ID immediately, so cancellation can be reconciled.
                    await lease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                last = update;
                yield return new(update) { ResponseId = responseId, CreatedAt = createdAt };
                if (item.Data == "[DONE]") break;
            }
            if (last is null || !decoder.Completed || decoder.MessageId is null || state.ConversationId is null)
                throw new SdkException("Stream ended without a confirmed conversation and assistant response.", "incomplete_web_response");
            var input = messages.Select(m => new StoredMessage(m.Id, m.Role, m.Text) { Content = m.Content, Metadata = m.Metadata }).ToArray();
            var response = new WebResponseRecord(responseId, state.ConversationId, decoder.MessageId,
                request.Model, last.Text, createdAt, state.LastResponseId, input) { Assets = decoder.Assets };
            if (truncateAt is { } at) state.History.RemoveRange(at, state.History.Count - at);
            state.History.AddRange(input);
            state.History.Add(new(response.MessageId, "assistant", response.Text));
            state.ParentMessageId = response.MessageId;
            state.LastResponseId = response.Id;
            state.Responses.Add(response);
            if (!request.InternalMcpTurn && state.VisibleHistory is { } visible)
            {
                visible.AddRange(input);
                visible.Add(new(response.MessageId, "assistant", response.Text));
            }
            state.PendingMessages.Clear();
            state.PreviousAssistantBeforePendingTurn = null;
            state.RequiresReconciliation = false;
            await lease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            saved = true;
            if ((request.TemporaryChat ? _temporary : store) is IResponseScopeIndex index)
                await index.RecordResponseScopeAsync(scope, responseId, CancellationToken.None).ConfigureAwait(false);
            yield return new(last with { Delta = "", Completed = true }, response) { ResponseId = responseId, CreatedAt = createdAt };
        }
        finally
        {
            // State was saved before send. A disposed, cancelled or failed iterator leaves a durable recovery marker.
            if (!saved) state.RequiresReconciliation = true;
        }
    }

    public async Task<ConversationState> GetStateAsync(ConversationScope scope, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ConversationState>(JsonSerializer.Serialize(lease.State))!;
    }

    public async Task ValidateContextAsync(ConversationScope scope, WebChatContext context, CancellationToken ct = default)
    {
        var id = context.ResolveGizmoId();
        var state = await GetStateAsync(scope, ct).ConfigureAwait(false);
        if (state.TemporaryChat is { } temporary && temporary != context.TemporaryChat)
            throw new SdkException("Temporary and persistent chats require different thread IDs.", "conversation_mode_conflict", HttpStatusCode.Conflict);
        ValidateContext(state, id, context.TemporaryChat);
    }

    private static void ValidateContext(ConversationState state, string? requestedGizmo, bool temporary)
    {
        if (temporary && state.ProjectId is not null)
            throw new SdkException("Temporary chats cannot belong to projects.", "temporary_project_conflict", HttpStatusCode.BadRequest);
        if (requestedGizmo is not null && (state.GizmoId is not null && state.GizmoId != requestedGizmo
            || state.GizmoId is null && (state.ConversationId is not null || state.History.Count > 0)))
            throw new SdkException("This thread belongs to another ChatGPT project or chat context. Select a new thread ID.", "project_binding_conflict", HttpStatusCode.Conflict);
    }

    public async Task<WebResponseRecord> GetResponseAsync(ConversationScope scope, string id, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        return lease.State.Responses.FirstOrDefault(r => r.Id == id)
            ?? throw new SdkException("Response not found in this account/user/thread.", "response_not_found", HttpStatusCode.NotFound);
    }

    public async Task<ConversationScope> ResolveResponseScopeAsync(ConversationScope scope, string responseId, CancellationToken ct = default)
    {
        if ((await GetStateAsync(scope, ct).ConfigureAwait(false)).Responses.Any(r => r.Id == responseId)) return scope;
        var found = await _temporary.FindResponseScopeAsync(scope.AccountId, scope.UserId, responseId, ct).ConfigureAwait(false);
        if (found is null && store is IResponseScopeIndex index) found = await index.FindResponseScopeAsync(scope.AccountId, scope.UserId, responseId, ct).ConfigureAwait(false);
        if (found is not null) { await GetResponseAsync(found, responseId, ct).ConfigureAwait(false); return found; }
        throw new SdkException("Response not found for this application user.", "response_not_found", HttpStatusCode.NotFound);
    }

    public async Task DeleteResponseAsync(ConversationScope scope, string id, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        if (lease.State.Responses.RemoveAll(r => r.Id == id) == 0) throw new SdkException("Response not found.", "response_not_found", HttpStatusCode.NotFound);
        if (lease.State.LastResponseId == id) lease.State.LastResponseId = null;
        await lease.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateConversationAsync(ConversationScope scope, string? title = null, bool? archived = null, bool delete = false, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        var state = lease.State;
        if (state.RequiresReconciliation) throw new ConversationReconciliationException();
        var id = state.ConversationId ?? throw new ArgumentException("No linked conversation.");
        if (state.TemporaryChat == true && (title is not null || archived is not null))
            throw new UnsupportedWebFeatureException("renaming or archiving temporary chats, which have no persistent history resource");
        if (title is not null) await transport.RenameConversationAsync(scope.AccountId, id, title, ct).ConfigureAwait(false);
        if (archived is not null) await transport.ArchiveConversationAsync(scope.AccountId, id, archived.Value, ct).ConfigureAwait(false);
        if (!delete) return;
        try { await transport.DeleteConversationAsync(scope.AccountId, id, ct).ConfigureAwait(false); }
        catch (SdkException ex) when (state.TemporaryChat == true && ex.StatusCode == HttpStatusCode.NotFound)
        { /* Temporary chats can already be absent from the backend's persistent history resource. Clear only the owned in-memory binding. */ }
        state.ConversationId = null; state.ParentMessageId = "client-created-root"; state.LastResponseId = null;
        state.History.Clear(); state.Responses.Clear(); state.PendingMessages.Clear(); state.PreviousAssistantBeforePendingTurn = null;
        state.VisibleHistory = null; state.McpExecutions.Clear();
        await lease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        SetChatMcp(scope, null);
    }

    public async Task LinkAsync(ConversationScope scope, string conversationId, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, false, ct).ConfigureAwait(false);
        if (lease.State.ConversationId is not null || lease.State.RequiresReconciliation || lease.State.History.Count > 0)
            throw new SdkException("This thread is already bound. Select a new thread ID before linking another conversation.", "thread_already_linked", HttpStatusCode.Conflict);
        var remote = await transport.GetConversationAsync(scope.AccountId, conversationId, ct).ConfigureAwait(false);
        ImportBranch(lease.State, remote, conversationId);
        await store.ClaimRemoteConversationAsync(scope, conversationId, ct).ConfigureAwait(false);
        await lease.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(ConversationScope scope, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        var state = lease.State;
        if (!state.RequiresReconciliation) return;
        if (state.ConversationId is null)
            throw new SdkException("The interrupted first send did not return a conversation ID. Find the conversation in ChatGPT and use ResolveUnknownConversationAsync with its ID.", "unknown_remote_conversation", HttpStatusCode.Conflict);
        var remote = await transport.GetConversationAsync(scope.AccountId, state.ConversationId, ct).ConfigureAwait(false);
        VerifyPendingBranch(state, remote);
        ImportBranch(state, remote, state.ConversationId);
        state.PendingMessages.Clear();
        state.PreviousAssistantBeforePendingTurn = null;
        state.RequiresReconciliation = false;
        await lease.SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ResolveUnknownConversationAsync(ConversationScope scope, string conversationId, CancellationToken ct = default)
    {
        await using var lease = await LeaseAsync(scope, null, ct).ConfigureAwait(false);
        var state = lease.State;
        if (!state.RequiresReconciliation || state.ConversationId is not null) throw new InvalidOperationException("Only an interrupted first send with an unknown remote ID can be resolved here.");
        var remote = await transport.GetConversationAsync(scope.AccountId, conversationId, ct).ConfigureAwait(false);
        VerifyPendingBranch(state, remote);
        await store.ClaimRemoteConversationAsync(scope, conversationId, ct).ConfigureAwait(false);
        ImportBranch(state, remote, conversationId);
        state.RequiresReconciliation = false;
        state.PendingMessages.Clear();
        state.PreviousAssistantBeforePendingTurn = null;
        await lease.SaveAsync(ct).ConfigureAwait(false);
    }

    private static List<System.Text.Json.Nodes.JsonNode> Branch(System.Text.Json.Nodes.JsonNode remote)
    {
        var mapping = remote["mapping"] as System.Text.Json.Nodes.JsonObject ?? throw new SdkException("Conversation has no message mapping.", "invalid_web_conversation");
        var current = remote["current_node"]?.GetValue<string>() ?? throw new SdkException("Conversation has no current node.", "invalid_web_conversation");
        var nodes = new List<System.Text.Json.Nodes.JsonNode>();
        var visited = new HashSet<string>();
        while (current is not null)
        {
            if (!visited.Add(current) || mapping[current] is not { } node) throw new SdkException("Invalid conversation branch.", "invalid_web_conversation");
            nodes.Add(node);
            current = node["parent"]?.GetValue<string>();
        }
        nodes.Reverse();
        return nodes;
    }

    private static void VerifyPendingBranch(ConversationState state, System.Text.Json.Nodes.JsonNode remote)
    {
        var branch = Branch(remote);
        var ids = branch.Select(n => n["message"]?["id"]?.GetValue<string>()).ToHashSet();
        // ChatGPT can redact a hidden user's message while retaining its node on the current branch.
        bool HiddenNodeOnBranch(WebInputMessage message) => message.Metadata?["is_visually_hidden_from_conversation"]?.GetValue<bool>() == true &&
            remote["mapping"]?[message.Id] is { } node && branch.Any(n => ReferenceEquals(n, node));
        if (state.PendingMessages.Any(m => !ids.Contains(m.Id) && !HiddenNodeOnBranch(m)))
            throw new SdkException("The remote branch does not contain every pending message ID. Resolve the remote branch before appending; no resend was attempted.", "pending_turn_not_found", HttpStatusCode.Conflict);
        var last = branch.LastOrDefault(n => n["message"] is not null)?["message"];
        if (state.PreviousAssistantBeforePendingTurn is { } previous && last?["id"]?.GetValue<string>() == previous)
            throw new SdkException("The remote branch has not confirmed a new regenerated reply.", "remote_generation_incomplete", HttpStatusCode.Conflict);
        var finishedAssistant = last?["author"]?["role"]?.GetValue<string>() == "assistant" && last["end_turn"]?.GetValue<bool>() == true && last["status"]?.GetValue<string>() == "finished_successfully";
        if (!finishedAssistant && !WebStreamDecoder.IsFinishedImageOutput(last))
            throw new SdkException("The pending generation has not finished successfully on the remote branch.", "remote_generation_incomplete", HttpStatusCode.Conflict);
    }

    private static void ImportBranch(ConversationState state, System.Text.Json.Nodes.JsonNode remote, string id)
    {
        var nodes = Branch(remote);
        if (remote["is_temporary_chat"]?.GetValue<bool>() == true && state.TemporaryChat != true)
            throw new SdkException("A temporary conversation cannot be imported into persistent history.", "conversation_mode_conflict", HttpStatusCode.Conflict);
        var remoteGizmo = remote["gizmo_id"]?.GetValue<string>() ?? remote["conversation_mode"]?["gizmo_id"]?.GetValue<string>();
        if (remoteGizmo is not null)
        {
            new WebChatContext { GizmoId = remoteGizmo, TemporaryChat = state.TemporaryChat == true }.ResolveGizmoId();
            if (state.GizmoId is not null && state.GizmoId != remoteGizmo)
                throw new SdkException("The remote conversation belongs to a different project or gizmo.", "project_binding_conflict", HttpStatusCode.Conflict);
            state.GizmoId = remoteGizmo;
        }
        state.ConversationId = id;
        state.ParentMessageId = remote["current_node"]!.GetValue<string>();
        state.History = nodes.Select(n => n["message"]).Where(m => m is not null)
            .Where(m => m!["author"]?["role"]?.GetValue<string>() is "user" or "assistant" || WebStreamDecoder.IsFinishedImageOutput(m))
            .Where(m => m!["channel"]?.GetValue<string>() is null or "final")
            .Where(m => m!["metadata"]?["is_visually_hidden_from_conversation"]?.GetValue<bool>() != true && m["content"]?["content_type"]?.GetValue<string>() is "text" or "multimodal_text")
            .Select(m => new StoredMessage(m!["id"]!.GetValue<string>(), m["author"]!["role"]!.GetValue<string>() == "tool" ? "assistant" : m["author"]!["role"]!.GetValue<string>(),
                m["content"]?["parts"] is System.Text.Json.Nodes.JsonArray parts ? string.Concat(parts.OfType<System.Text.Json.Nodes.JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>())) : "")
                { Content = m["content"]?["content_type"]?.GetValue<string>() == "multimodal_text" ? m["content"]?.DeepClone() as JsonObject : null,
                    Metadata = m["content"]?["content_type"]?.GetValue<string>() == "multimodal_text" ? m["metadata"]?.DeepClone() as JsonObject : null })
            .ToList();
        // Imported remote messages have no official API response objects. Previously recorded objects remain retrievable.
        state.LastResponseId = state.Responses.LastOrDefault(r => r.MessageId == state.ParentMessageId)?.Id;
    }
}
