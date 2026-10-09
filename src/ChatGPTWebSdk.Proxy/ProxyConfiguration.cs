using System.Text.Json;
using ChatGPTWebSdk.Web;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Mcp;

namespace ChatGPTWebSdk.Proxy;

public sealed class ProxyConfiguration
{
    public Uri BaseUri { get; init; } = new("https://chatgpt.com/");
    public WebEndpointProfile Profile { get; init; } = WebEndpointProfile.Modern;
    public string Mode { get; init; } = "hybrid";
    public string HttpDriver { get; init; } = "systemCurl";
    public BrowserSentinelOptions Browser { get; init; } = new();
    public List<ProxyAccount> Accounts { get; init; } = [];
    public List<ProxyClient> Clients { get; init; } = [];
    public string SessionDirectory { get; init; } = ".sessions";
    public Dictionary<string, string> ModelAliases { get; init; } = [];
    public bool IgnoreModelRestrictions { get; init; }
    public McpConversationOptions Mcp { get; init; } = new();
    public static ProxyConfiguration Load()
    {
        var path = Environment.GetEnvironmentVariable("CHATGPT_WEB_CONFIG");
        ProxyConfiguration config;
        if (!string.IsNullOrWhiteSpace(path))
            config = JsonSerializer.Deserialize<ProxyConfiguration>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Empty proxy config.");
        else
        {
            var key = Environment.GetEnvironmentVariable("CHATGPT_WEB_PROXY_KEY") ?? throw new InvalidOperationException("Set CHATGPT_WEB_CONFIG or CHATGPT_WEB_PROXY_KEY.");
            config = new()
            {
                Accounts = [new() { Id = "default", Credentials = new()
                {
                    AccessToken = Environment.GetEnvironmentVariable("CHATGPT_WEB_ACCESS_TOKEN"),
                    CookieHeader = Environment.GetEnvironmentVariable("CHATGPT_WEB_COOKIE"),
                    UserAgent = Environment.GetEnvironmentVariable("CHATGPT_WEB_USER_AGENT") ?? "ChatGPTWebSdk/0.1"
                } }],
                Clients = [new() { ApiKey = key, AccountId = "default", UserId = Environment.GetEnvironmentVariable("CHATGPT_WEB_PROXY_USER") ?? "default" }],
                SessionDirectory = Environment.GetEnvironmentVariable("CHATGPT_WEB_SESSION_DIRECTORY") ?? ".sessions"
            };
        }
        if (config.Clients.Count == 0 || config.Accounts.Count == 0 || config.Clients.Any(c => c.ApiKey.Length < 16 || !config.Accounts.Any(a => a.Id == c.AccountId)))
            throw new InvalidDataException("Configure accounts and clients; every client needs an API key of at least 16 characters and a configured account.");
        if (config.Accounts.Select(a => a.Id).Distinct().Count() != config.Accounts.Count || config.Clients.Select(c => c.ApiKey).Distinct().Count() != config.Clients.Count)
            throw new InvalidDataException("Account IDs and client API keys must be unique.");
        if (config.Mode == "browserOnly") throw new NotSupportedException("Browser-only mode is reserved and is not implemented.");
        if (config.Mode is not ("apiOnly" or "hybrid") || config.HttpDriver is not ("systemCurl" or "dotNet")) throw new InvalidDataException("Invalid web mode or HTTP driver.");
        return config;
    }
}

public sealed class ProxyAccount
{
    public required string Id { get; init; }
    public required WebCredentials Credentials { get; init; }
}
public sealed class ProxyClient
{
    public required string ApiKey { get; init; }
    public required string AccountId { get; init; }
    public required string UserId { get; init; }
    public bool AllowConversationLinking { get; init; }
    public List<string> McpServers { get; init; } = [];
}
