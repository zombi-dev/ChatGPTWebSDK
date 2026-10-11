using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChatGPTWebSdk.Storage;

namespace ChatGPTWebSdk.Mcp;

[JsonConverter(typeof(JsonStringEnumConverter<McpWebTransport>))]
public enum McpWebTransport { Auto, StreamableHttp, Sse, Stdio }

/// <summary>A server explicitly registered by the application. Connection secrets never enter model messages.</summary>
public sealed record McpServerConfiguration
{
    public required string Label { get; init; }
    public Uri? Endpoint { get; init; }
    public McpWebTransport Transport { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public bool RequireApproval { get; init; }
    /// <summary>Resume interrupted SSE responses using GET and Last-Event-ID; never repeats a tool POST.</summary>
    public int MaxReconnectionAttempts { get; init; } = 2;
    public TimeSpan ReconnectionInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    [JsonIgnore] public IMcpWebServer? Client { get; init; }

    internal void Validate()
    {
        if (!IsValidLabel(Label))
            throw new ArgumentException("MCP labels must contain 1..128 ASCII letters, digits, dots, dashes or underscores.");
        if (!Enum.IsDefined(Transport)) throw new ArgumentException("Unknown MCP transport.");
        if (MaxReconnectionAttempts is < 0 or > 10 || ReconnectionInterval < TimeSpan.Zero || ReconnectionInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid MCP SSE resumption limits.");
        var sources = (Endpoint is null ? 0 : 1) + (Command is null ? 0 : 1) + (Client is null ? 0 : 1);
        if (sources != 1) throw new ArgumentException("Each MCP server needs exactly one endpoint, command or existing client.");
        if (Endpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0))
            throw new ArgumentException("MCP endpoints must be absolute HTTP(S) URLs without user information or fragments.");
        if (Endpoint is not null && Transport == McpWebTransport.Stdio || Command is not null && Transport is not (McpWebTransport.Stdio or McpWebTransport.Auto))
            throw new ArgumentException("The MCP transport does not match its connection settings.");
        if (Command is not null && (string.IsNullOrWhiteSpace(Command) || Command.Any(char.IsControl))) throw new ArgumentException("Invalid MCP command.");
        if (AllowedTools?.Any(string.IsNullOrWhiteSpace) == true || AllowedTools?.Distinct(StringComparer.Ordinal).Count() != AllowedTools?.Count)
            throw new ArgumentException("MCP tool allowlists require unique, nonempty names.");
        if (Headers.Any(h => string.IsNullOrWhiteSpace(h.Key) || h.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || h.Value.Any(c => c is '\r' or '\n')))
            throw new ArgumentException("Invalid MCP headers.");
    }

    internal static bool IsValidLabel(string? label) => !string.IsNullOrWhiteSpace(label) && label.Length <= 128 &&
        label.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}

public sealed class McpConversationOptions
{
    public IReadOnlyList<McpServerConfiguration> Servers { get; init; } = [];
    public int MaxToolRounds { get; init; } = 8;
    public int MaxToolCalls { get; init; } = 32;
    public int MaxManifestCharacters { get; init; } = 256 * 1024;
    public int MaxToolArgumentsCharacters { get; init; } = 64 * 1024;
    public int MaxToolResultCharacters { get; init; } = 512 * 1024;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Keep owned connections between messages in the same account/user/thread. Disable for per-message processes.</summary>
    public bool ReuseConnections { get; init; } = true;
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxConnections { get; init; } = 32;
    /// <summary>Retries initialization and tool discovery only. Tool execution is never retried.</summary>
    public int DiscoveryRetries { get; init; } = 1;
    public TimeSpan DiscoveryRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxProtocolRepairs { get; init; } = 1;
    [JsonIgnore] public Func<ConversationScope, string, bool>? IsServerAllowed { get; init; }
    [JsonIgnore] public Func<McpToolCallContext, CancellationToken, ValueTask<bool>>? ApproveToolCall { get; init; }
    [JsonIgnore] public IProgress<McpToolProgress>? Progress { get; init; }

    internal void Validate()
    {
        if (MaxToolRounds is < 1 or > 100 || MaxToolCalls is < 1 or > 1000 || MaxManifestCharacters is < 128 or > 4 * 1024 * 1024 ||
            MaxToolArgumentsCharacters is < 128 or > 4 * 1024 * 1024 || MaxToolResultCharacters is < 128 or > 4 * 1024 * 1024 || ToolTimeout <= TimeSpan.Zero || ToolTimeout > TimeSpan.FromHours(1))
            throw new ArgumentException("Invalid MCP conversation limits.");
        if (ConnectionTimeout <= TimeSpan.Zero || ConnectionTimeout > TimeSpan.FromHours(1) || ConnectionIdleTimeout <= TimeSpan.Zero || ConnectionIdleTimeout > TimeSpan.FromHours(1) ||
            MaxConnections is < 1 or > 256 || DiscoveryRetries is < 0 or > 3 || DiscoveryRetryDelay < TimeSpan.Zero || DiscoveryRetryDelay > TimeSpan.FromMinutes(1) || MaxProtocolRepairs is < 0 or > 3)
            throw new ArgumentException("Invalid MCP connection or repair limits.");
        foreach (var server in Servers) server.Validate();
        if (Servers.Select(s => s.Label).Distinct(StringComparer.Ordinal).Count() != Servers.Count) throw new ArgumentException("MCP server labels must be unique.");
    }
}

public sealed record McpWebTool(string Name, string? Description, JsonObject InputSchema, bool ReadOnly = false);
public sealed record McpToolCallContext(ConversationScope Scope, string CallId, string ServerLabel, string ToolName, JsonObject Arguments)
{
    /// <summary>The selected configuration, including scoped connection settings, for application approval and progress callbacks.</summary>
    [JsonIgnore] public McpServerConfiguration? Server { get; init; }
}
public sealed record McpToolProgress(McpToolCallContext Call, string Status, JsonObject? Result = null);

/// <summary>Optional adapter for an existing MCP client. The SDK does not dispose application-supplied clients.</summary>
public interface IMcpWebServer
{
    Task<IReadOnlyList<McpWebTool>> ListToolsAsync(CancellationToken ct = default);
    Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default);
}

/// <summary>Durable execution marker. An uncertain call must be resolved explicitly and is never resent automatically.</summary>
public sealed class McpToolExecution
{
    public required string Id { get; init; }
    public required string ServerLabel { get; init; }
    public required string ToolName { get; init; }
    public required JsonObject Arguments { get; init; }
    public string Status { get; set; } = "executing";
    public JsonObject? Result { get; set; }
    public bool Delivered { get; set; }
}

internal sealed record McpServerSelection(McpServerConfiguration Server, IReadOnlyList<string>? AllowedTools = null,
    bool? ReadOnly = null, JsonNode? Approval = null);
