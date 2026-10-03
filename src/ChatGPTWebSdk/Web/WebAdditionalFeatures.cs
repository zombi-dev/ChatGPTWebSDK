using System.Globalization;
using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Web;

public sealed record WebConversationQuery
{
    public int Offset { get; init; }
    public int Limit { get; init; } = 20;
    public string? Order { get; init; }
    public bool? Archived { get; init; }
    public bool? Starred { get; init; }
    public string? ConversationOrigin { get; init; }
    public string? ExcludeConversationOrigin { get; init; }
    public bool? Expand { get; init; }
}

public sealed record WebModelQuery
{
    public bool? IncludeIcons { get; init; }
    public bool? IsGizmo { get; init; }
    public bool? IncludeInactiveModels { get; init; }
}

public sealed record WebFileQuery
{
    public string? ConversationId { get; init; }
    public string? DownloadIntent { get; init; }
    public bool? Inline { get; init; }
    public bool? IncludeLibraryFileState { get; init; }
}

public sealed partial class ChatGptWebTransport
{
    public Task<JsonNode?> GetUserAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetUser", ct);
    public Task<JsonNode?> GetProfileAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetProfile", ct);
    public Task<JsonNode?> GetUserSettingsAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetUserSettings", ct);
    public Task<JsonNode?> GetAdultStatusAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetAdultStatus", ct);
    public Task<JsonNode?> GetGranularConsentAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetGranularConsent", ct);
    public Task<JsonNode?> GetOptimizedAccountsAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetOptimizedAccounts", ct);
    public Task<JsonNode?> GetBillingPageAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetBillingPage", ct);
    public Task<JsonNode?> GetPluginsHomeAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetPluginsHome", ct);
    public Task<JsonNode?> GetConnectorEligibilityAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetConnectorEligibility", ct);
    public Task<JsonNode?> GetDefaultTabRecommendationAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetDefaultTabRecommendation", ct);
    public Task<JsonNode?> GetCodexAccountsAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetCodexAccounts", ct);
    public Task<JsonNode?> GetCodexUsageAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetCodexUsage", ct);
    public Task<JsonNode?> GetCodexSitesAccessAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetCodexSitesAccess", ct);
    public Task<JsonNode?> GetNotificationConnectionAsync(string account, CancellationToken ct = default) => CapturedJson(account, "GetNotificationConnection", ct);
    public Task<JsonNode?> ClearSettingsCacheAsync(string account, CancellationToken ct = default) => CapturedJson(account, "ClearSettingsCache", ct);
    public Task<JsonNode?> GetSubscriptionsAsync(string account, string accountId, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetSubscriptions", query: new Dictionary<string, string?> { ["account_id"] = accountId }, ct: ct);
    public Task<JsonNode?> ListNotificationsAsync(string account, int limit = 20, CancellationToken ct = default) => CapturedList(account, "ListNotifications", limit, ct);
    public Task<JsonNode?> ListComposerItemsAsync(string account, int limit = 20, string? entrypoint = null, CancellationToken ct = default) =>
        CapturedList(account, "ListComposerItems", limit, ct, new() { ["entrypoint"] = entrypoint });
    public Task<JsonNode?> ListInstalledPluginsAsync(string account, int limit = 20, bool includeExtensions = true, CancellationToken ct = default) =>
        CapturedList(account, "ListInstalledPlugins", limit, ct, new() { ["includeExtensions"] = Boolean(includeExtensions) });
    public Task<JsonNode?> ListCodexTasksAsync(string account, int limit = 20, string? taskFilter = null, CancellationToken ct = default) =>
        CapturedList(account, "ListCodexTasks", limit, ct, new() { ["task_filter"] = taskFilter });
    public Task<JsonNode?> ListPinsAsync(string account, string? itemType = null, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "ListPins", query: new Dictionary<string, string?> { ["item_type"] = itemType }, ct: ct);
    public Task<JsonNode?> GetConnectorPermissionsAsync(string account, string? scope = null, bool includePermissions = true, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetConnectorPermissions", query: new Dictionary<string, string?> { ["scope"] = scope, ["include_permissions"] = Boolean(includePermissions) }, ct: ct);
    public Task<JsonNode?> GetModelsAsync(string account, WebModelQuery options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetModels", query: new Dictionary<string, string?> { ["iim"] = NullableBoolean(options.IncludeInactiveModels), ["include_icons"] = NullableBoolean(options.IncludeIcons), ["is_gizmo"] = NullableBoolean(options.IsGizmo) }, ct: ct);
    public Task<JsonNode?> GetAlternateModelsAsync(string account, bool? includeIcons = null, bool? includeInactiveModels = null, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetAlternateModels", query: new Dictionary<string, string?> { ["iim"] = NullableBoolean(includeInactiveModels), ["include_icons"] = NullableBoolean(includeIcons) }, ct: ct);
    public Task<JsonNode?> GetPromptLibraryAsync(string account, string modelSlug, int limit = 4, bool useV2 = true, CancellationToken ct = default) =>
        CapturedList(account, "GetPromptLibrary", limit, ct, new() { ["model_slug"] = modelSlug, ["use_v2"] = Boolean(useV2) });
    public Task<JsonNode?> ListConversationsAsync(string account, WebConversationQuery options, CancellationToken ct = default)
    {
        if (options.Offset < 0) throw new ArgumentOutOfRangeException(nameof(options.Offset));
        return CapturedList(account, "ListConversations", options.Limit, ct, new()
        {
            ["offset"] = options.Offset.ToString(CultureInfo.InvariantCulture), ["order"] = options.Order,
            ["is_archived"] = NullableBoolean(options.Archived), ["is_starred"] = NullableBoolean(options.Starred), ["expand"] = NullableBoolean(options.Expand),
            ["conversation_origin"] = options.ConversationOrigin, ["exclude_conversation_origin"] = options.ExcludeConversationOrigin
        });
    }
    public Task<JsonNode?> GetConversationTurnsAsync(string account, string conversationId, int numTurns = 20, bool includeHasVersions = true, CancellationToken ct = default)
    {
        ProjectLimit(numTurns);
        return CapturedOperations.SendJsonAsync(account, "GetConversationTurns", new Dictionary<string, string> { ["conversation_id"] = conversationId },
            new Dictionary<string, string?> { ["num_turns"] = numTurns.ToString(CultureInfo.InvariantCulture), ["include_has_versions"] = Boolean(includeHasVersions) }, ct: ct);
    }
    public Task<JsonNode?> GetConversationsBatchAsync(string account, IReadOnlyList<string> conversationIds, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetConversationsBatch", body: IdentifiersBody("conversation_ids", conversationIds), ct: ct);
    public Task<JsonNode?> GetAppsBatchAsync(string account, IReadOnlyList<string> appIds, bool includeTools = true, CancellationToken ct = default)
    {
        var body = IdentifiersBody("app_ids", appIds); body["include_tools"] = includeTools;
        return CapturedOperations.SendJsonAsync(account, "GetAppsBatch", body: body, ct: ct);
    }
    public Task<JsonNode?> InitializeConversationAsync(string account, JsonObject options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "InitializeConversation", body: options, ct: ct);
    public Task<JsonNode?> ListAccessibleConnectorLinksAsync(string account, JsonObject options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "ListAccessibleConnectorLinks", body: options, ct: ct);
    public Task<JsonNode?> SubmitComparisonFeedbackAsync(string account, JsonObject feedback, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "SubmitComparisonFeedback", body: feedback, ct: ct);
    public Task<JsonNode?> GetSystemHintsAsync(string account, IReadOnlyDictionary<string, string?> options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetSystemHints", query: options, ct: ct);
    public Task<JsonNode?> GetFileAsync(string account, string fileId, WebFileQuery options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetFile", new Dictionary<string, string> { ["file_id"] = fileId }, new Dictionary<string, string?> { ["conversation_id"] = options.ConversationId }, ct: ct);
    public Task<JsonNode?> GetFileDownloadInfoAsync(string account, string fileId, WebFileQuery options, CancellationToken ct = default) =>
        CapturedOperations.SendJsonAsync(account, "GetFileDownloadInfo", new Dictionary<string, string> { ["file_id"] = fileId }, new Dictionary<string, string?>
        { ["conversation_id"] = options.ConversationId, ["download_intent"] = options.DownloadIntent, ["inline"] = NullableBoolean(options.Inline), ["include_library_file_state"] = NullableBoolean(options.IncludeLibraryFileState) }, ct: ct);
    private Task<JsonNode?> CapturedJson(string account, string operation, CancellationToken ct) => CapturedOperations.SendJsonAsync(account, operation, ct: ct);
    private Task<JsonNode?> CapturedList(string account, string operation, int limit, CancellationToken ct, Dictionary<string, string?>? query = null)
    {
        ProjectLimit(limit); query ??= []; query["limit"] = limit.ToString(CultureInfo.InvariantCulture);
        return CapturedOperations.SendJsonAsync(account, operation, query: query, ct: ct);
    }
    private static string? NullableBoolean(bool? value) => value is { } actual ? Boolean(actual) : null;
    private static JsonObject IdentifiersBody(string name, IReadOnlyList<string> ids)
    {
        if (ids.Count is < 1 or > 100 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl))) throw new ArgumentException("Provide between one and 100 valid identifiers.", nameof(ids));
        return new() { [name] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
    }
}
