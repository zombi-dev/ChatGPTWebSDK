using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OpenAI;

namespace ChatGPTWebSdk.Tests;

public sealed class OpenAiTests
{
    public static IEnumerable<object[]> Operations() => ApiCatalog.Current.Operations.Select(op => new object[] { op.Id });
    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_generated_REST_operation_routes_with_required_parameters_and_media_type(string id)
    {
        var operation = ApiCatalog.Current.Get(id);
        Uri? sentUri = null;
        string? sentMethod = null;
        using var handler = new RecordingHandler((request, ct) =>
        {
            sentUri = request.RequestUri;
            sentMethod = request.Method.Method;
            Assert.Equal("fixture-api-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(FakeWebHandler.Json(new JsonObject { ["ok"] = true }));
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, "fixture-api-key");
        HttpContent? content = null;
        JsonNode? body = null;
        if (operation.RequiresBody)
        {
            if (operation.RequestMediaTypes.Contains("application/json")) body = new JsonObject();
            else
            {
                content = new ByteArrayContent([1, 2]);
                content.Headers.ContentType = new MediaTypeHeaderValue(operation.RequestMediaTypes.First());
            }
        }
        var request = new ApiRequest
        {
            Path = operation.RequiredPath.ToDictionary(n => n, _ => "id with / & spaces"),
            Query = operation.RequiredQuery.ToDictionary(n => n, _ => (string?)"query & /"),
            Headers = operation.RequiredHeaders.ToDictionary(n => n, _ => "fixture"),
            Body = body, Content = content
        };
        var name = string.Concat(id.Split(['_', '-', '.']).Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Async";
        var method = typeof(OpenAiOperationsClient).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        using var result = await (Task<ApiResult>)method.Invoke(client.Operations, [request, CancellationToken.None])!;
        Assert.Equal(operation.Method, sentMethod);
        Assert.Equal("api.openai.com", sentUri!.Host);
        Assert.DoesNotContain("{", sentUri.AbsoluteUri);
        Assert.DoesNotContain("}", sentUri.AbsoluteUri);
        Assert.DoesNotContain("query &", sentUri.Query);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }
    [Fact]
    public void Catalog_is_the_fingerprinted_supplied_spec()
    {
        Assert.Equal(353, ApiCatalog.Current.Operations.Length);
        Assert.Equal("2cf225eb2eb480bf7d57e04e3e7d3198fe7f47c49bc5defb53bf86966706c5f9", ApiCatalog.Current.Sha256);
        foreach (var family in new[] { "/responses", "/chat/completions", "/embeddings", "/audio/speech", "/files", "/images/generations", "/batches", "/models" })
            Assert.Contains(ApiCatalog.Current.Operations, op => op.Path == family);
    }
    [Fact]
    public async Task Official_files_upload_uses_real_multipart_content()
    {
        string? body = null, contentType = null;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            contentType = request.Content.Headers.ContentType!.MediaType;
            return FakeWebHandler.Json(new JsonObject { ["id"] = "file-fixture" });
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, "fixture-key");
        using var result = await client.Files.UploadAsync(new MemoryStream([10, 20]), "fixture.bin", "user_data");
        Assert.Equal("multipart/form-data", contentType);
        Assert.Contains("fixture.bin", body);
        Assert.Contains("user_data", body);
    }
    [Fact]
    public async Task Missing_required_path_is_rejected_before_any_network_request()
    {
        var calls = 0;
        using var handler = new RecordingHandler((request, ct) => { calls++; return Task.FromResult(FakeWebHandler.Json(new JsonObject())); });
        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, "fixture-key");
        await Assert.ThrowsAsync<ArgumentException>(() => client.Operations.GetResponseAsync());
        Assert.Equal(0, calls);
    }
    [Fact]
    public void Typed_responses_preserve_arbitrary_official_fields_and_reject_identity_overrides()
    {
        var request = new ResponseRequest { Model = "fixture-model", Input = JsonValue.Create("hello")!, Extra = new() { ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }) } };
        Assert.Equal("web_search", request.ToJson()["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => new ResponseRequest { Input = JsonValue.Create("hello")!, Extra = new() { ["input"] = JsonValue.Create("overridden") } }.ToJson());
    }
}

