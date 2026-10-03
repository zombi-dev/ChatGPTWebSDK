using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.OpenAI;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Proxy;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))) builder.WebHost.UseUrls("http://127.0.0.1:5088");
var config = ProxyConfiguration.Load();
using var http = config.HttpDriver == "systemCurl" ? WebHttpClient.CreateCurl() : WebHttpClient.Create();
builder.Services.AddSingleton<IConversationStore>(new FileConversationStore(config.SessionDirectory));
builder.Services.AddSingleton(new ChatGptWebTransport(http, new StaticWebCredentialProvider(config.Accounts.ToDictionary(a => a.Id, a => a.Credentials)), new() { BaseUri = config.BaseUri, Endpoints = config.Profile, SentinelSessionProvider = config.Mode == "hybrid" ? new BrowserSentinelProvider(config.Browser) : null }));
builder.Services.AddSingleton<ChatGptWebClient>();
builder.Services.AddSingleton(sp => new OpenAiWebAdapter(sp.GetRequiredService<ChatGptWebClient>(), config.ModelAliases));
var app = builder.Build();
var client = app.Services.GetRequiredService<ChatGptWebClient>();
var adapter = app.Services.GetRequiredService<OpenAiWebAdapter>();

app.Use(async (ctx, next) =>
{
    try
    {
        var supplied = ctx.Request.Headers.Authorization.ToString();
        if (!supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) throw new SdkException("A proxy API key is required.", "authentication_required", System.Net.HttpStatusCode.Unauthorized);
        var key = Encoding.UTF8.GetBytes(supplied[7..]);
        var identity = config.Clients.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(key, Encoding.UTF8.GetBytes(c.ApiKey)))
            ?? throw new SdkException("Invalid proxy API key.", "authentication_required", System.Net.HttpStatusCode.Unauthorized);
        var thread = ctx.Request.Headers["X-ChatGPT-Thread-Id"].FirstOrDefault() ?? "default";
        ctx.Items["scope"] = new ConversationScope(identity.AccountId, identity.UserId, thread);
        ctx.Items["allowLink"] = identity.AllowConversationLinking;
        await next(ctx);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        var sdk = ex as SdkException;
        var message = sdk?.Message ?? (ex is ArgumentException or JsonException or InvalidOperationException ? "Invalid request body or parameters." : "The upstream request failed; no automatic resend was attempted.");
        var error = new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = sdk?.Code ?? "request_error", ["code"] = sdk?.Code ?? "request_error", ["param"] = null } };
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = (int)(sdk?.StatusCode ?? (ex is ArgumentException or JsonException or InvalidOperationException ? System.Net.HttpStatusCode.BadRequest : System.Net.HttpStatusCode.BadGateway));
            await ctx.Response.WriteAsJsonAsync(error, ctx.RequestAborted);
        }
        else if (!ctx.RequestAborted.IsCancellationRequested)
        {
            await ctx.Response.WriteAsync("event: error\ndata: " + error.ToJsonString() + "\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
});

ConversationScope Scope(HttpContext ctx) => (ConversationScope)ctx.Items["scope"]!;
async Task<JsonObject> Body(HttpContext ctx) => await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject ?? throw new ArgumentException("Expected a JSON object.");
using var compatibility = new HttpMessageInvoker(new OpenAiWebHttpHandler(adapter, (key, thread) =>
{
    var identity = config.Clients.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(c.ApiKey)))
        ?? throw new SdkException("Invalid proxy API key.", "authentication_required", System.Net.HttpStatusCode.Unauthorized);
    return new(identity.AccountId, identity.UserId, thread ?? "default");
}));
app.MapMethods("/v1/{**path}", ["GET", "POST", "DELETE", "PATCH", "PUT"], async (HttpContext ctx) =>
{
    using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), "http://127.0.0.1" + ctx.Request.Path + ctx.Request.QueryString);
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding")) request.Content = new StreamContent(ctx.Request.Body);
    foreach (var header in ctx.Request.Headers)
        if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    using var response = await compatibility.SendAsync(request, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers.Concat(response.Content.Headers))
        if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) ctx.Response.Headers[header.Key] = header.Value.ToArray();
    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

app.MapGet("/web/binding", async Task<IResult> (HttpContext ctx) =>
{
    var state = await client.GetStateAsync(Scope(ctx), ctx.RequestAborted);
    return Results.Json(new { state.Scope, state.ConversationId, state.ParentMessageId, state.LastResponseId, state.RequiresReconciliation, state.Revision, messageCount = state.History.Count });
});
app.MapPost("/web/link", async Task<IResult> (HttpContext ctx) =>
{
    if (!(bool)ctx.Items["allowLink"]!) throw new SdkException("External conversation linking requires an explicitly configured client permission.", "external_conversation_link_denied", System.Net.HttpStatusCode.Forbidden);
    var body = await Body(ctx);
    await client.LinkAsync(Scope(ctx), body["conversation_id"]?.GetValue<string>() ?? throw new ArgumentException("conversation_id is required."), ctx.RequestAborted);
    return Results.Ok(new { linked = true });
});
app.MapPost("/web/reconcile", async Task<IResult> (HttpContext ctx) =>
{
    var body = await Body(ctx);
    if (body["conversation_id"]?.GetValue<string>() is { } id) await client.ResolveUnknownConversationAsync(Scope(ctx), id, ctx.RequestAborted);
    else await client.ReconcileAsync(Scope(ctx), ctx.RequestAborted);
    return Results.Ok(new { reconciled = true });
});
app.MapPatch("/web/conversation", async Task<IResult> (HttpContext ctx) =>
{
    var body = await Body(ctx);
    if (body.Any(p => p.Key is not ("title" or "is_archived" or "delete"))) throw new ArgumentException("Unknown conversation property.");
    await client.UpdateConversationAsync(Scope(ctx), body["title"]?.GetValue<string>(), body["is_archived"]?.GetValue<bool>(), body["delete"]?.GetValue<bool>() == true, ctx.RequestAborted);
    return Results.Ok(new { updated = true });
});
app.MapPost("/web/edit", async (HttpContext ctx) =>
{
    var body = await Body(ctx);
    var result = await client.EditAsync(Scope(ctx), body["message_id"]?.GetValue<string>() ?? throw new ArgumentException("message_id is required."),
        body["text"]?.GetValue<string>() ?? throw new ArgumentException("text is required."), body["model"]?.GetValue<string>() ?? throw new ArgumentException("model is required."), ctx.RequestAborted);
    return Results.Json(OpenAiWebAdapter.ResponseJson(result.Response));
});
app.MapPost("/web/regenerate", async (HttpContext ctx) =>
{
    var body = await Body(ctx);
    var result = await client.RegenerateAsync(Scope(ctx), body["model"]?.GetValue<string>() ?? throw new ArgumentException("model is required."), ctx.RequestAborted);
    return Results.Json(OpenAiWebAdapter.ResponseJson(result.Response));
});
app.MapGet("/capabilities", () => Results.Json(new
{
    transport = "chatgpt-web-http", mode = config.Mode, httpDriver = config.HttpDriver, liveVerified = false,
    spec = new { ApiCatalog.Current.SpecVersion, ApiCatalog.Current.Sha256, operationCount = ApiCatalog.Current.Operations.Length },
    webMappings = new[] { "responses.create", "responses.retrieve/delete/input_items (local)", "chat.completions.create", "models.list/retrieve", "files.create/retrieve/list/content", "images.generate/edit (one image)", "conversations.create/retrieve/update/delete/items.list/items.retrieve", "conversation.link/rename/archive/delete/reconcile/edit/regenerate" },
    unsupportedBehavior = "Unmapped API operations and controls return 501. No platform API fallback or automatic generation retry."

}));
app.MapFallback((Func<IResult>)(() => throw new UnsupportedWebFeatureException("requested endpoint")));
await app.RunAsync();

public partial class Program { }
