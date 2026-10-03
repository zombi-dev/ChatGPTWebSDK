using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Storage;

public sealed record ConversationScope
{
    public string AccountId { get; }
    public string UserId { get; }
    public string ThreadId { get; }
    public ConversationScope(string accountId, string userId, string threadId = "default")
    {
        foreach (var value in new[] { accountId, userId, threadId })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Account, user and thread IDs must be nonempty, at most 512 characters, and contain no control characters.");
        AccountId = accountId; UserId = userId; ThreadId = threadId;
    }
    public string StorageKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { AccountId, UserId, ThreadId })))).ToLowerInvariant();
}

public sealed record StoredMessage(string Id, string Role, string Text)
{
    public System.Text.Json.Nodes.JsonObject? Content { get; init; }
    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }
}
public sealed record WebResponseRecord(string Id, string ConversationId, string MessageId, string Model, string Text,
    long CreatedAt, string? PreviousResponseId, IReadOnlyList<StoredMessage> Input)
{
    public IReadOnlyList<WebAsset> Assets { get; init; } = [];
}

public sealed class ConversationState
{
    public int SchemaVersion { get; init; } = 1;
    public required ConversationScope Scope { get; init; }
    public long Revision { get; set; }
    public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string? ConversationId { get; set; }
    public string ParentMessageId { get; set; } = Guid.NewGuid().ToString();
    public string? LastResponseId { get; set; }
    public bool RequiresReconciliation { get; set; }
    public bool? TemporaryChat { get; set; }
    public string? GizmoId { get; set; }
    public string? ProjectId => GizmoId?.StartsWith("g-p-", StringComparison.Ordinal) == true ? GizmoId : null;
    public string? PreviousAssistantBeforePendingTurn { get; set; }
    public string? ConversationAlias { get; set; }
    public System.Text.Json.Nodes.JsonObject Metadata { get; set; } = new();
    public Dictionary<string, WebUploadedFile> Files { get; set; } = new();
    public List<WebInputMessage> PendingMessages { get; set; } = [];
    public List<StoredMessage> History { get; set; } = [];
    public List<WebResponseRecord> Responses { get; set; } = [];
    // Remote history includes MCP control turns. This transcript contains application-visible turns only.
    public List<StoredMessage>? VisibleHistory { get; set; }
    public List<Mcp.McpToolExecution> McpExecutions { get; set; } = [];
}

public interface IConversationLease : IAsyncDisposable
{
    ConversationState State { get; }
    Task SaveAsync(CancellationToken ct = default);
}

public interface IConversationStore
{
    ValueTask<IConversationLease> AcquireAsync(ConversationScope scope, CancellationToken ct = default);
    Task ClaimRemoteConversationAsync(ConversationScope scope, string conversationId, CancellationToken ct = default);
}

/// <summary>Optional per-user index for retrieving Responses created on another thread.</summary>
public interface IResponseScopeIndex
{
    Task RecordResponseScopeAsync(ConversationScope scope, string responseId, CancellationToken ct = default);
    Task<ConversationScope?> FindResponseScopeAsync(string accountId, string userId, string responseId, CancellationToken ct = default);
}

public sealed class InMemoryConversationStore : IConversationStore, IResponseScopeIndex
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, string> _states = new();
    private readonly ConcurrentDictionary<string, string> _owners = new();
    private readonly ConcurrentDictionary<string, ConversationScope> _responseScopes = new();
    private static string ResponseKey(string account, string user, string id) => JsonSerializer.Serialize(new[] { account, user, id });
    public Task RecordResponseScopeAsync(ConversationScope scope, string responseId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); _responseScopes[ResponseKey(scope.AccountId, scope.UserId, responseId)] = scope; return Task.CompletedTask; }
    public Task<ConversationScope?> FindResponseScopeAsync(string accountId, string userId, string responseId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(_responseScopes.GetValueOrDefault(ResponseKey(accountId, userId, responseId))); }
    public Task ClaimRemoteConversationAsync(ConversationScope scope, string conversationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = JsonSerializer.Serialize(new[] { scope.AccountId, conversationId });
        if (_owners.GetOrAdd(key, scope.UserId) != scope.UserId)
            throw new Protocol.SdkException("This remote conversation is already assigned to another application user.", "conversation_owner_conflict", System.Net.HttpStatusCode.Forbidden);
        return Task.CompletedTask;
    }
    public async ValueTask<IConversationLease> AcquireAsync(ConversationScope scope, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(scope.StorageKey, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _states.TryGetValue(scope.StorageKey, out var json) ? JsonSerializer.Deserialize<ConversationState>(json)! : new() { Scope = scope };
            return new Lease(state, gate, () => _states[scope.StorageKey] = JsonSerializer.Serialize(state));
        }
        catch { gate.Release(); throw; }
    }
    private sealed class Lease(ConversationState state, SemaphoreSlim gate, Action save) : IConversationLease
    {
        private bool _disposed;
        public ConversationState State => state;
        public Task SaveAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            state.Revision++;
            save();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; gate.Release(); }
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class FileConversationStore : IConversationStore, IResponseScopeIndex
{
    private readonly string _directory;
    private readonly TimeSpan _lockTimeout;
    public FileConversationStore(string directory, TimeSpan? lockTimeout = null)
    {
        _directory = Path.GetFullPath(directory);
        _lockTimeout = lockTimeout ?? TimeSpan.FromMinutes(5);
        Directory.CreateDirectory(_directory);
    }
    private string ResponsePath(string account, string user, string id) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { account, user, id })))).ToLowerInvariant() + ".response-index.json");
    public async Task RecordResponseScopeAsync(ConversationScope scope, string responseId, CancellationToken ct = default)
    {
        var path = ResponsePath(scope.AccountId, scope.UserId, responseId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(scope), ct).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<ConversationScope?> FindResponseScopeAsync(string accountId, string userId, string responseId, CancellationToken ct = default)
    {
        var path = ResponsePath(accountId, userId, responseId);
        if (!File.Exists(path)) return null;
        var scope = JsonSerializer.Deserialize<ConversationScope>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        if (scope is null || scope.AccountId != accountId || scope.UserId != userId) throw new InvalidDataException("Response index scope mismatch.");
        return scope;
    }
    public async Task ClaimRemoteConversationAsync(ConversationScope scope, string conversationId, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { scope.AccountId, conversationId })))).ToLowerInvariant();
        var path = Path.Combine(_directory, "remote-" + key + ".owner.json");
        var started = System.Diagnostics.Stopwatch.StartNew();
        FileStream? handle = null;
        while (handle is null)
        {
            ct.ThrowIfCancellationRequested();
            try { handle = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < _lockTimeout) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        await using (handle.ConfigureAwait(false))
        {
            if (File.Exists(path))
            {
                if (JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) != scope.UserId)
                    throw new Protocol.SdkException("This remote conversation is already assigned to another application user.", "conversation_owner_conflict", System.Net.HttpStatusCode.Forbidden);
            }
            else
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(scope.UserId), ct).ConfigureAwait(false);
                    File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
    public async ValueTask<IConversationLease> AcquireAsync(ConversationScope scope, CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, scope.StorageKey + ".json");
        var lockPath = path + ".lock";
        var started = System.Diagnostics.Stopwatch.StartNew();
        FileStream? handle = null;
        while (handle is null)
        {
            ct.ThrowIfCancellationRequested();
            try { handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (started.Elapsed < _lockTimeout) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        try
        {
            var state = File.Exists(path) ? JsonSerializer.Deserialize<ConversationState>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))! : new() { Scope = scope };
            if (state.SchemaVersion != 1 || state.Scope != scope) throw new InvalidDataException("Conversation state schema or scope mismatch.");
            return new Lease(state, path, handle);
        }
        catch { await handle.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class Lease(ConversationState state, string path, FileStream handle) : IConversationLease
    {
        private bool _disposed;
        public ConversationState State => state;
        public async Task SaveAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            state.Revision++;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(file, state, cancellationToken: ct).ConfigureAwait(false);
                    await file.FlushAsync(ct).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public async ValueTask DisposeAsync()
        {
            if (!_disposed) { _disposed = true; await handle.DisposeAsync().ConfigureAwait(false); }
        }
    }
}

