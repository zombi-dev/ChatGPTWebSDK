using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace ChatGPTWebSdk.Mcp;

internal sealed class McpServerConnection(IMcpWebServer server, IAsyncDisposable? owner = null) : IAsyncDisposable
{
    public IMcpWebServer Server => server;
    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? ValueTask.CompletedTask;

    public static async Task<McpServerConnection> ConnectAsync(McpServerConfiguration configuration, TimeSpan timeout, CancellationToken ct)
    {
        if (configuration.Client is { } existing) return new(existing);
        IClientTransport transport;
        HttpClient? http = null;
        if (configuration.Command is { } command)
        {
            // Pass arguments separately. Never turn server configuration into shell script text.
            transport = new StdioClientTransport(new()
            {
                Name = configuration.Label, Command = command, Arguments = configuration.Arguments.ToArray(), WorkingDirectory = configuration.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?>(StdioClientTransportOptions.GetDefaultEnvironmentVariables().Concat(configuration.Environment).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)),
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            });
        }
        else
        {
            // MCP credentials belong exclusively to this connection; redirects must not forward them.
            http = new(new OriginGuard(configuration.Endpoint!, new SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
            transport = new HttpClientTransport(new()
            {
                Name = configuration.Label, Endpoint = configuration.Endpoint!, ConnectionTimeout = timeout,
                AdditionalHeaders = configuration.Headers.ToDictionary(p => p.Key, p => p.Value),
                TransportMode = configuration.Transport switch { McpWebTransport.Sse => HttpTransportMode.Sse, McpWebTransport.StreamableHttp => HttpTransportMode.StreamableHttp, _ => HttpTransportMode.AutoDetect },
                EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0
            }, http, ownsHttpClient: true);
        }
        try
        {
            var client = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "ChatGPTWebSdk", Version = typeof(McpServerConnection).Assembly.GetName().Version!.ToString(3) },
                ProtocolVersion = "2025-11-25", InitializationTimeout = timeout
            }, cancellationToken: ct).ConfigureAwait(false);
            return new(new OfficialClient(client), client);
        }
        catch { if (transport is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false); http?.Dispose(); throw; }
    }

    private sealed class OriginGuard(Uri endpoint, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var target = request.RequestUri;
            if (target is null || target.Scheme != endpoint.Scheme || target.IdnHost != endpoint.IdnHost || target.Port != endpoint.Port || target.UserInfo.Length > 0)
                throw new InvalidOperationException("An MCP server attempted to move the authenticated connection to another origin.");
            return base.SendAsync(request, ct);
        }
    }

    private sealed class OfficialClient(McpClient client) : IMcpWebServer
    {
        public async Task<IReadOnlyList<McpWebTool>> ListToolsAsync(CancellationToken ct = default) =>
            (await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false)).Select(t =>
                new McpWebTool(t.Name, t.Description, JsonNode.Parse(t.JsonSchema.GetRawText())!.AsObject(), t.ProtocolTool.Annotations?.ReadOnlyHint == true)).ToArray();
        public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default)
        {
            var values = arguments.ToDictionary(p => p.Key, p => (object?)JsonSerializer.Deserialize<JsonElement>(p.Value?.ToJsonString() ?? "null"));
            var result = await client.CallToolAsync(name, values, cancellationToken: ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToNode(result, McpJsonUtilities.DefaultOptions)!.AsObject();
        }
    }
}
