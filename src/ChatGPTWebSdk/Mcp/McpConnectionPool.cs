using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Resources;
using ChatGPTWebSdk.Storage;
using ModelContextProtocol.Client;

namespace ChatGPTWebSdk.Mcp;

internal sealed class McpConnectionPool(McpConversationOptions options) : IAsyncDisposable
{
    private readonly AsyncLeasePool<McpServerConnection> _pool = new(options.ReuseConnections, options.ConnectionIdleTimeout, options.MaxConnections);

    internal static string Key(ConversationScope scope, McpServerConfiguration server) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        scope.AccountId, scope.UserId, scope.ThreadId, server.Label, server.Endpoint, server.Command, server.Transport,
        server.Arguments, server.WorkingDirectory, server.MaxReconnectionAttempts, server.ReconnectionInterval,
        Environment = server.Environment.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
        Headers = server.Headers.Select(p => new KeyValuePair<string, string>(p.Key.ToLowerInvariant(), p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
    })));

    internal async Task<Connection> AcquireAsync(ConversationScope scope, McpServerConfiguration server, CancellationToken ct)
    {
        if (server.Client is { } supplied)
            return new(supplied, await supplied.ListToolsAsync(ct).WaitAsync(ct).ConfigureAwait(false));
        for (var attempt = 0; ; attempt++)
        {
            AsyncLeasePool<McpServerConnection>.Lease? lease = null;
            try
            {
                lease = await _pool.AcquireAsync(Key(scope, server), token => McpServerConnection.ConnectAsync(server, options.ConnectionTimeout, token), ct).ConfigureAwait(false);
                var tools = await lease.Resource.Server.ListToolsAsync(ct).WaitAsync(ct).ConfigureAwait(false);
                return new(lease.Resource.Server, tools, lease);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < options.DiscoveryRetries && IsTransient(ex))
            {
                if (lease is not null) { lease.Invalidate(); await lease.DisposeAsync().ConfigureAwait(false); }
                // Only initialization/discovery is retried. No tools/call request has been sent.
                await Task.Delay(options.DiscoveryRetryDelay, ct).ConfigureAwait(false);
            }
            catch
            {
                if (lease is not null) { lease.Invalidate(); await lease.DisposeAsync().ConfigureAwait(false); }
                throw;
            }
        }
    }

    internal static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        ClientTransportClosedException or IOException => true,
        _ => error.InnerException is { } inner && IsTransient(inner)
    };

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    internal sealed class Connection(IMcpWebServer server, IReadOnlyList<McpWebTool> tools,
        AsyncLeasePool<McpServerConnection>.Lease? lease = null) : IMcpWebServer, IAsyncDisposable
    {
        internal IReadOnlyList<McpWebTool> Tools => tools;
        public Task<IReadOnlyList<McpWebTool>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult(tools);
        public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default)
        {
            try { return await server.CallToolAsync(name, arguments, ct).WaitAsync(ct).ConfigureAwait(false); }
            catch { lease?.Invalidate(); throw; }
        }
        public ValueTask DisposeAsync() => lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
