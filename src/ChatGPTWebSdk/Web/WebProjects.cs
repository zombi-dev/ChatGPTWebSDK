using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Web;

public sealed partial class ChatGptWebTransport
{
    public Task<JsonNode> ListProjectsAsync(string account, int limit = 20, int conversationsPerProject = 5, bool ownedOnly = false, CancellationToken ct = default)
    {
        ProjectLimit(limit);
        if (conversationsPerProject is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(conversationsPerProject));
        return SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.ProjectsSidebar + $"?limit={limit}&conversations_per_gizmo={conversationsPerProject}&owned_only={Boolean(ownedOnly)}", ct: ct);
    }
    public Task<JsonNode> GetProjectAsync(string account, string projectId, bool includeFileLimits = true, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, ProjectPath(_options.Endpoints.ProjectById, projectId) + "?include_file_limits=" + Boolean(includeFileLimits), ct: ct);
    public Task<JsonNode> ListProjectConversationsAsync(string account, string projectId, int limit = 20, bool ownedOnly = false, CancellationToken ct = default)
    {
        ProjectLimit(limit);
        return SendJsonAsync(account, HttpMethod.Get, ProjectPath(_options.Endpoints.ProjectConversations, projectId) + $"?limit={limit}&owned_only={Boolean(ownedOnly)}", ct: ct);
    }
    public Task<JsonNode> ListProjectConnectorScopesAsync(string account, string projectId, int limit = 20, CancellationToken ct = default) =>
        ProjectListAsync(account, _options.Endpoints.ProjectConnectorScopes, projectId, limit, ct);
    public Task<JsonNode> ListProjectSavesAsync(string account, string projectId, int limit = 20, CancellationToken ct = default) =>
        ProjectListAsync(account, _options.Endpoints.ProjectSaves, projectId, limit, ct);
    private Task<JsonNode> ProjectListAsync(string account, string endpoint, string project, int limit, CancellationToken ct)
    {
        ProjectLimit(limit);
        return SendJsonAsync(account, HttpMethod.Get, ProjectPath(endpoint, project) + $"?limit={limit}", ct: ct);
    }
    private static void ProjectLimit(int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string ProjectPath(string endpoint, string id)
    {
        WebChatContext.ValidateProjectId(id);
        return endpoint.Replace("{project_id}", id, StringComparison.Ordinal);
    }
}
