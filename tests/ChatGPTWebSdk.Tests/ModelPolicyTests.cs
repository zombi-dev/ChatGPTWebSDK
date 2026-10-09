using System.Net;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Chat;

namespace ChatGPTWebSdk.Tests;

public sealed class ModelPolicyTests
{
    private static readonly ConversationScope Scope = new("account", "alice");
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static WebModelPolicy Before(bool bypass = false) => new(bypass, new Clock(WebModelPolicy.Gpt55Retirement.AddTicks(-1)));
    public static IEnumerable<object[]> Families()
    {
        foreach (var slug in new[] { "gpt-6", "gpt-6-instant", "gpt-6-thinking", "gpt-5-6", "gpt-5-6-instant", "gpt-5-6-thinking", "gpt-5.6-sol-wm",
            "gpt-5-5", "gpt-5-5-instant", "gpt-5-5-thinking", "gpt-5.5-wm" })
            foreach (var offset in new[] { -1L, 0L, 1L, TimeSpan.TicksPerDay * 365L }) yield return [slug, offset];
    }
    [Theory, MemberData(nameof(Families))]
    public void Retirement_applies_at_the_exact_UTC_boundary_and_only_to_GPT55(string slug, long offset)
    {
        var retiring = slug.StartsWith("gpt-5-5", StringComparison.Ordinal) || slug == "gpt-5.5-wm";
        var policy = new WebModelPolicy(timeProvider: new Clock(WebModelPolicy.Gpt55Retirement.AddTicks(offset)));
        Assert.Equal(!retiring || offset < 0, policy.IsAllowed(slug));
        if (retiring && offset >= 0) Assert.Equal("model_restricted", Assert.Throws<SdkException>(() => policy.Resolve(slug)).Code);
        else Assert.Equal(slug, policy.Resolve(slug));
    }
    [Theory]
    [InlineData("GPT-6", "gpt-6")]
    [InlineData("GPT-5.6 Sol", "gpt-5-6")]
    [InlineData("gpt-5.6-sol", "gpt-5-6")]
    [InlineData("GPT-5.5", "gpt-5-5")]
    public void Display_names_resolve_to_observed_web_slugs(string name, string expected) => Assert.Equal(expected, Before().Resolve(name));

    public static IEnumerable<object[]> Rejected()
    {
        foreach (var slug in new[] { "gpt-6.1-sol-wm", "gpt-6-sol-wm", "gpt-6-astra-wm", "gpt-6-luna-wm", "gpt-5.6-luna-wm", "gpt-5.6-terra-wm",
            "gpt-5-6-mini", "research", "gpt-4o", "unknown-model", "gpt-6-thinking-evil", "gpt-5-6-unknown" })
            foreach (var bypass in new[] { false, true }) yield return [slug, bypass];
    }
    [Theory, MemberData(nameof(Rejected))]
    public void Unknown_and_other_web_families_require_explicit_bypass(string slug, bool bypass)
    {
        var policy = Before(bypass);
        if (bypass) Assert.Equal(slug, policy.Resolve(slug));
        else Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<SdkException>(() => policy.Resolve(slug)).StatusCode);
    }
    [Theory]
    [InlineData(false, "")][InlineData(true, "")][InlineData(false, " ")][InlineData(true, " ")]
    public void Bypass_still_requires_a_model(bool bypass, string slug) => Assert.Throws<ArgumentException>(() => Before(bypass).Resolve(slug));

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Retirement_can_be_bypassed_without_changing_the_clock(bool bypass)
    {
        var policy = new WebModelPolicy(bypass, new Clock(WebModelPolicy.Gpt55Retirement.AddYears(1)));
        if (bypass) Assert.Equal("gpt-5-5", policy.Resolve("GPT-5.5"));
        else Assert.Throws<SdkException>(() => policy.Resolve("GPT-5.5"));
    }

    [Theory]
    [InlineData("append", false)][InlineData("append", true)][InlineData("edit", false)][InlineData("edit", true)]
    [InlineData("regenerate", false)][InlineData("regenerate", true)]
    public async Task Native_turns_enforce_bypass_without_poisoning_the_conversation(string action, bool bypass)
    {
        using var handler = new FakeWebHandler(); var client = handler.Client(options: new() { IgnoreModelRestrictions = bypass });
        var first = await client.SendAsync(Scope, "original", "gpt-6");
        handler.Remote = handler.CompletedRemote(handler.Turns[0].Body);
        Task<WebChatResult> Send() => action switch
        {
            "edit" => client.EditAsync(Scope, first.Response.Input[0].Id, "edited", "unknown-model"),
            "regenerate" => client.RegenerateAsync(Scope, "unknown-model"),
            _ => client.SendAsync(Scope, "next", "unknown-model")
        };
        if (bypass) { await Send(); Assert.Equal("unknown-model", handler.Turns.Last().Body["model"]!.GetValue<string>()); }
        else
        {
            Assert.Equal("model_restricted", (await Assert.ThrowsAsync<SdkException>(Send)).Code);
            Assert.Single(handler.Turns); Assert.False((await client.GetStateAsync(Scope)).RequiresReconciliation);
            await client.SendAsync(Scope, "still usable", "gpt-6"); Assert.Equal(2, handler.Turns.Count);
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Raw_generation_cannot_bypass_the_transport_policy(bool bypass)
    {
        using var handler = new FakeWebHandler(); var transport = handler.Client(options: new() { IgnoreModelRestrictions = bypass }).Transport;
        var body = new JsonObject { ["model"] = "unknown-model" };
        if (bypass) { using var stream = await transport.OpenTurnAsync("account", body); await foreach (var _ in stream.ReadAsync()) { } Assert.Single(handler.Turns); }
        else { Assert.Equal("model_restricted", (await Assert.ThrowsAsync<SdkException>(() => transport.OpenTurnAsync("account", body))).Code); Assert.Empty(handler.Turns); }
    }
    [Theory]
    [InlineData("chat", false)][InlineData("chat", true)][InlineData("responses", false)][InlineData("responses", true)]
    public async Task Runtime_options_enforce_model_restrictions_in_official_client_types(string api, bool bypass)
    {
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler);
        using var runtime = new ChatGPTWebRuntime(new()
        {
            Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "fixture-token" }), AccountId = "account", Mode = ChatGPTWebMode.ApiOnly,
            Endpoints = new(), ConversationStore = new InMemoryConversationStore(), HttpClient = http, IgnoreModelRestrictions = bypass
        });
        async Task Send()
        {
            if (api == "chat") await runtime.CreateClient().GetChatClient("unknown-model").CompleteChatAsync("hello");
            else await runtime.CreateClient().GetResponsesClient().CreateResponseAsync("unknown-model", "hello");
        }
        if (bypass) { await Send(); Assert.Single(handler.Turns); }
        else { var error = await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(Send); Assert.Equal(400, error.Status); Assert.Empty(handler.Turns); }
    }
    [Theory]
    [InlineData("gpt-6", false)][InlineData("unknown-model", false)][InlineData("unknown-model", true)]
    public void Custom_aliases_are_checked_after_resolution(string target, bool bypass)
    {
        using var handler = new FakeWebHandler(); var adapter = new OpenAiWebAdapter(handler.Client(options: new() { IgnoreModelRestrictions = bypass }), new Dictionary<string, string> { ["app-model"] = target });
        WebTurnRequest Prepare() => adapter.PrepareChat(new() { ["model"] = "app-model", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }) });
        if (target == "gpt-6" || bypass) Assert.Equal(target, Prepare().Model); else Assert.Throws<SdkException>(Prepare);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Restricted_image_requests_are_rejected_before_upload(bool responses)
    {
        var calls = 0; using var handler = new RecordingHandler((r, c) => { calls++; return Task.FromResult(FakeWebHandler.Json(new JsonObject())); });
        using var http = new HttpClient(handler); var adapter = new OpenAiWebAdapter(new(new(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" })), new InMemoryConversationStore()));
        var content = new JsonArray(new JsonObject { ["type"] = responses ? "input_image" : "image_url", ["image_url"] = "data:image/png;base64,AAAA" });
        var body = new JsonObject { ["model"] = "unknown-model" };
        if (responses) body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
        else body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content });
        await Assert.ThrowsAsync<SdkException>(async () => { if (responses) await adapter.PrepareResponseAsync(Scope, body); else await adapter.PrepareChatAsync(Scope, body); }); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Compatible_listing_is_filtered_but_raw_discovery_remains_complete(bool bypass)
    {
        using var handler = new RecordingHandler((r, c) => Task.FromResult(FakeWebHandler.Json(new JsonObject { ["models"] = new JsonArray(new JsonObject { ["slug"] = "gpt-6" }, new JsonObject { ["slug"] = "unknown-model" }) })));
        using var http = new HttpClient(handler); var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }), new() { IgnoreModelRestrictions = bypass });
        var adapter = new OpenAiWebAdapter(new(transport, new InMemoryConversationStore()));
        Assert.Equal(2, ((JsonArray)(await transport.GetModelsAsync("account"))["models"]!).Count);
        Assert.Equal(bypass ? 2 : 1, ((JsonArray)(await adapter.ListModelsAsync(Scope))["data"]!).Count);
    }
}
