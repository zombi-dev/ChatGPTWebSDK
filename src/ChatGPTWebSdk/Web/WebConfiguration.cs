using System.Net;
using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Web;

public sealed class WebCredentials
{
    public string? AccessToken { get; init; }
    public string? CookieHeader { get; init; }
    public IReadOnlyList<WebCookie> Cookies { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public string UserAgent { get; init; } = "ChatGPTWebSdk/1.0";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public WebSentinelSession? SentinelSession { get; init; }
    public override string ToString() => "[WebCredentials redacted]";
}

public interface IWebCredentialProvider
{
    ValueTask<WebCredentials> GetAsync(string accountId, CancellationToken ct = default);
}

public sealed class StaticWebCredentialProvider : IWebCredentialProvider
{
    private readonly IReadOnlyDictionary<string, WebCredentials> _accounts;
    public StaticWebCredentialProvider(string accountId, WebCredentials credentials) : this(new Dictionary<string, WebCredentials> { [accountId] = credentials }) { }
    public StaticWebCredentialProvider(IReadOnlyDictionary<string, WebCredentials> accounts) => _accounts = accounts;
    public ValueTask<WebCredentials> GetAsync(string accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _accounts.TryGetValue(accountId, out var value) ? ValueTask.FromResult(value)
            : throw new Protocol.SdkException("No web credentials configured for this account.", "authentication_required", HttpStatusCode.Unauthorized);
    }
}

public sealed class WebEndpointProfile
{
    public string Session { get; init; } = "api/auth/session";
    public string Conversation { get; init; } = "backend-api/conversation";
    public string ConversationById { get; init; } = "backend-api/conversation/{conversation_id}";
    public string Conversations { get; init; } = "backend-api/conversations";
    public string Models { get; init; } = "backend-api/models";
    public string Requirements { get; init; } = "backend-api/sentinel/chat-requirements";
    public string? RequirementsPrepare { get; init; }
    public string? RequirementsFinalize { get; init; }
    public string? ConversationPrepare { get; init; }
    public string DeleteConversation { get; init; } = "backend-api/conversation/id/{conversation_id}";
    public string Files { get; init; } = "backend-api/files";
    public string ProcessUpload { get; init; } = "backend-api/files/process_upload_stream";
    public string FileById { get; init; } = "backend-api/files/{file_id}/simple";
    public string FileDownload { get; init; } = "backend-api/files/download/{file_id}";
    public string Accounts { get; init; } = "backend-api/accounts/check";
    public string PromptLibrary { get; init; } = "backend-api/prompt_library/";
    public string GenerateTitle { get; init; } = "backend-api/conversation/gen_title/{conversation_id}";
    public string Shares { get; init; } = "backend-api/shared_conversations";
    public string ShareCreate { get; init; } = "backend-api/share/create";
    public string ShareById { get; init; } = "backend-api/share/{share_id}";
    public string ProjectsSidebar { get; init; } = "backend-api/gizmos/snorlax/sidebar";
    public string ProjectById { get; init; } = "backend-api/gizmos/{project_id}";
    public string ProjectConversations { get; init; } = "backend-api/gizmos/{project_id}/conversations";
    public string ProjectConnectorScopes { get; init; } = "backend-api/projects/{project_id}/connector_scopes";
    public string ProjectSaves { get; init; } = "backend-api/projects/{project_id}/saves";
    public JsonObject RequirementsBody { get; init; } = new();
    // Captured non-identity defaults; the SDK always replaces messages, model and conversation IDs.
    public JsonObject ConversationDefaults { get; init; } = new();
    public static WebEndpointProfile Modern => new()
    {
        Conversation = "backend-api/f/conversation",
        ConversationPrepare = "backend-api/f/conversation/prepare",
        RequirementsPrepare = "backend-api/sentinel/chat-requirements/prepare",
        RequirementsFinalize = "backend-api/sentinel/chat-requirements/finalize",
        Accounts = "backend-api/accounts/check/v4-2023-04-27",
        ConversationDefaults = new() { ["supported_encodings"] = new JsonArray("v1"), ["is_do_not_remember"] = false }
    };
}

public sealed class WebClientOptions
{
    public Uri BaseUri { get; init; } = new("https://chatgpt.com/");
    public WebEndpointProfile Endpoints { get; init; } = new();
    public bool FetchRequirements { get; init; } = true;
    public IWebRequestHeaderProvider? RequestHeaderProvider { get; init; }
    public IWebRequirementsBodyProvider? RequirementsBodyProvider { get; init; }
    public IWebSentinelChallengeProvider? SentinelChallengeProvider { get; init; }
    public IWebSentinelSessionProvider? SentinelSessionProvider { get; init; }
    public int MaxEventCharacters { get; init; } = 4 * 1024 * 1024;
}

public sealed record WebRequestContext(string AccountId, JsonObject Body, JsonNode? Requirements);

public interface IWebRequestHeaderProvider
{
    ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(WebRequestContext context, CancellationToken ct = default);
}

public interface IWebRequirementsBodyProvider
{
    ValueTask<JsonObject> GetBodyAsync(string accountId, JsonObject turnBody, CancellationToken ct = default);
}

public static class WebHttpClient
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromMinutes(5) };
    public static HttpClient CreateCurl(string executable = "curl") => new(new CurlHttpMessageHandler(executable)) { Timeout = TimeSpan.FromMinutes(5) };
}

