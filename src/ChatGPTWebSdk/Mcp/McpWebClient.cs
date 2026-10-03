namespace ChatGPTWebSdk.Mcp;

/// <summary>An explicit, reusable connection to a local or remote MCP server.</summary>
public sealed class McpWebClient : IMcpWebServer, IAsyncDisposable
{
    private readonly McpServerConnection _connection;
    private bool _disposed;
    private McpWebClient(McpServerConnection connection) => _connection = connection;
    public static async Task<McpWebClient> ConnectAsync(McpServerConfiguration server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Validate();
        return new(await McpServerConnection.ConnectAsync(server, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false));
    }
    public Task<IReadOnlyList<McpWebTool>> ListToolsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.Server.ListToolsAsync(ct);
    }
    public Task<System.Text.Json.Nodes.JsonObject> CallToolAsync(string name, System.Text.Json.Nodes.JsonObject arguments, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.Server.CallToolAsync(name, arguments, ct);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
