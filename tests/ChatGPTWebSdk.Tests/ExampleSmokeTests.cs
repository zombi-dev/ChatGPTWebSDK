using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Web;
using QuickStart;

namespace ChatGPTWebSdk.Tests;

public sealed class ExampleSmokeTests
{
    private static string Authentication() => WebAuthentication.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["version"] = 1, ["origin"] = "https://chatgpt.com", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"), ["accessToken"] = "fixture-secret-token",
        ["userAgent"] = "fixture-browser", ["cookies"] = new JsonArray(), ["headers"] = new JsonObject()
    }.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task Actual_example_verifies_two_turns_SSE_and_temporary_context_without_exposing_content()
    {
        string? marker = null;
        using var handler = new FakeWebHandler
        {
            StreamFactory = (body, turn) =>
            {
                marker ??= Regex.Match(body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>(), "SDKCHECK[0-9a-f]{32}").Value;
                return FakeWebHandler.Event(FakeWebHandler.Snapshot("example-conversation", "assistant-" + turn, marker, true)) + "data: [DONE]\n\n";
            }
        };
        using var http = new HttpClient(handler);
        var report = await SmokeCheck.RunAsync(Authentication(), new(), apiOnly: true, http: http, endpoints: new());
        Assert.Equal("passed", report["status"]!.GetValue<string>()); Assert.Equal(2, handler.Turns.Count);
        Assert.True(handler.Turns.All(t => t.Body["history_and_training_disabled"]!.GetValue<bool>()));
        Assert.Equal("example-conversation", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.Single((JsonArray)handler.Turns[1].Body["messages"]!);
        Assert.True(report["models"]![0]!["streaming"]!.GetValue<bool>());
        Assert.DoesNotContain(marker!, report.ToJsonString()); Assert.DoesNotContain("fixture-secret-token", report.ToJsonString());
    }
    [Theory]
    [InlineData(401, "blocked")][InlineData(403, "blocked")][InlineData(429, "blocked")][InlineData(404, "failed")][InlineData(500, "failed")]
    public async Task Discovery_distinguishes_authentication_and_access_blocks_from_endpoint_failure(int status, string expected)
    {
        using var handler = new RecordingHandler((r, c) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("fixture-secret-token and private conversation") }));
        using var http = new HttpClient(handler);
        var report = await SmokeCheck.RunAsync(Authentication(), new(), apiOnly: true, http: http);
        Assert.Equal(expected, report["status"]!.GetValue<string>());
        Assert.DoesNotContain("fixture-secret-token", report.ToJsonString()); Assert.DoesNotContain("private conversation", report.ToJsonString());
    }
    [Theory]
    [InlineData(null, "authentication_missing")][InlineData("", "authentication_missing")][InlineData("not-an-export", "authentication_invalid")]
    public async Task Missing_and_invalid_authentication_produce_a_sanitized_blocked_report(string? auth, string code)
    {
        var report = await SmokeCheck.RunAsync(auth, new(), apiOnly: true);
        Assert.Equal("blocked", report["status"]!.GetValue<string>()); Assert.Equal(code, report["code"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"models\":{}}")] [InlineData("{\"models\":[{}]}")]
    public async Task Changed_model_schema_is_a_confirmed_example_failure(string json)
    {
        using var handler = new RecordingHandler((r, c) => Task.FromResult(FakeWebHandler.Json(JsonNode.Parse(json)!))); using var http = new HttpClient(handler);
        var report = await SmokeCheck.RunAsync(Authentication(), new(), true, http);
        Assert.Equal("failed", report["status"]!.GetValue<string>()); Assert.Equal("unsupported_models_format", report["code"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(401, "blocked")][InlineData(403, "blocked")][InlineData(429, "blocked")][InlineData(404, "failed")][InlineData(500, "failed")]
    public async Task Model_generation_failures_are_classified_after_actual_official_client_requests(int status, string expected)
    {
        using var handler = new FakeWebHandler { TurnFailure = (HttpStatusCode)status }; using var http = new HttpClient(handler);
        var report = await SmokeCheck.RunAsync(Authentication(), new(), true, http, new());
        Assert.Equal(expected, report["status"]!.GetValue<string>()); Assert.Single((JsonArray)report["models"]!);
    }
    [Fact]
    public async Task A_reply_without_the_requested_marker_fails_the_example()
    {
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler);
        var report = await SmokeCheck.RunAsync(Authentication(), new(), true, http, new());
        Assert.Equal("failed", report["status"]!.GetValue<string>());
        Assert.Equal("example_first_turn_mismatch", report["models"]![0]!["code"]!.GetValue<string>());
    }
    [Fact]
    public void API_schema_hash_ignores_private_values_array_order_and_catalog_size_but_detects_structural_changes()
    {
        var first = JsonNode.Parse("{\"models\":[{\"slug\":\"gpt-6\",\"title\":\"private-one\"}],\"account\":\"secret-one\"}")!;
        var second = JsonNode.Parse("{\"account\":\"secret-two\",\"models\":[{\"title\":\"private-two\",\"slug\":\"gpt-5-6\"},{\"title\":\"private-three\",\"slug\":\"gpt-6\"}]}")!;
        Assert.Equal(SmokeCheck.ShapeHash(first), SmokeCheck.ShapeHash(second));
        second["models"]![0]!["capabilities"] = new JsonArray(); Assert.NotEqual(SmokeCheck.ShapeHash(first), SmokeCheck.ShapeHash(second));
    }
    [Theory]
    [InlineData(200, 200, "passed")][InlineData(404, 200, "degraded")][InlineData(404, 404, "failed")]
    [InlineData(401, 404, "blocked")][InlineData(401, 200, "degraded")]
    public async Task Real_example_checks_all_allowed_families_and_requires_proven_failure_for_every_family(int first, int second, string expected)
    {
        var markers = new Dictionary<string, string>();
        using var handler = new FakeWebHandler
        {
            ModelCatalog = new(new JsonObject { ["slug"] = "gpt-6" }, new JsonObject { ["slug"] = "gpt-5-6" }),
            TurnFailureSelector = body => (HttpStatusCode)(body["model"]!.GetValue<string>() == "gpt-6" ? first : second) is HttpStatusCode.OK ? null :
                (HttpStatusCode)(body["model"]!.GetValue<string>() == "gpt-6" ? first : second),
            StreamFactory = (body, turn) =>
            {
                var model = body["model"]!.GetValue<string>();
                if (!markers.ContainsKey(model)) markers[model] = Regex.Match(body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>(), "SDKCHECK[0-9a-f]{32}").Value;
                return FakeWebHandler.Event(FakeWebHandler.Snapshot("example-" + model, "assistant-" + turn, markers[model], true)) + "data: [DONE]\n\n";
            }
        };
        using var http = new HttpClient(handler); var report = await SmokeCheck.RunAsync(Authentication(), new(), true, http, new());
        Assert.Equal(expected, report["status"]!.GetValue<string>()); Assert.Equal(2, ((JsonArray)report["models"]!).Count);
        Assert.Equal((first == 200 ? 2 : 1) + (second == 200 ? 2 : 1), handler.Turns.Count);
    }
    [Fact]
    public async Task Cancellation_still_produces_a_blocked_report_for_the_scheduler()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        var report = await SmokeCheck.RunAsync(Authentication(), new(), true, ct: source.Token);
        Assert.Equal("blocked", report["status"]!.GetValue<string>()); Assert.Equal("request_timeout", report["code"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void One_string_initialization_supports_explicit_model_bypass(bool bypass)
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-model-init-" + Guid.NewGuid());
        try
        {
            using var runtime = global::OpenAI.ChatGPTWeb.Initialize(Authentication(), ignoreModelRestrictions: bypass, sessionDirectory: directory, mode: global::OpenAI.ChatGPTWebMode.ApiOnly);
            Assert.Equal(bypass, runtime.Web.Transport.ModelPolicy.IgnoreRestrictions);
        }
        finally { TestDirectory.Delete(directory); }
    }
}
