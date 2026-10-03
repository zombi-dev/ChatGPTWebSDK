using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class CapturedOperationTests
{
    public static IEnumerable<object[]> Operations() => WebCapturedOperationsClient.Operations.Select(o => new object[] { o.Id });
    private static Dictionary<string, string> Parameters(WebCapturedOperation operation) => System.Text.RegularExpressions.Regex.Matches(operation.Path, "\\{([a-z_]+)\\}")
        .ToDictionary(m => m.Groups[1].Value, m => m.Groups[1].Value == "project_id" ? "g-p-fixture" : "fixture/encoded?segment");
    private sealed class FreshAuthorization : IWebSentinelSessionProvider
    {
        public int Calls { get; private set; }
        public ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default)
        {
            Calls++; return ValueTask.FromResult(new WebAuthorizedSession(credentials, new() { Token = "fixture-fresh-" + Calls, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) }));
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Every_captured_operation_sends_its_method_path_query_and_body_to_the_configured_origin(string id)
    {
        var operation = WebCapturedOperationsClient.GetOperation(id);
        var parameters = Parameters(operation);
        var query = operation.QueryParameters.ToDictionary(k => k, _ => (string?)"encoded & value=✓");
        var expectedPath = System.Text.RegularExpressions.Regex.Replace(operation.Path, "\\{([a-z_]+)\\}", m => m.Groups[1].Value == "project_id" ? "g-p-fixture" : "fixture%2Fencoded%3Fsegment");
        if (query.Count > 0) expectedPath += "?" + string.Join("&", query.Keys.Order(StringComparer.Ordinal).Select(k => k + "=encoded%20%26%20value%3D%E2%9C%93"));
        var sentinel = new FreshAuthorization();
        var calls = 0;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            calls++;
            Assert.Equal("fixture.invalid", request.RequestUri!.Host);
            Assert.Equal(operation.Method, request.Method.Method);
            Assert.Equal("/" + expectedPath, request.RequestUri.PathAndQuery);
            Assert.Equal(id == "GetAuthSession" ? null : "fixture-auth", request.Headers.Authorization?.Parameter);
            if (operation.Method == "POST") Assert.Equal("fixture", (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!["payload"]!.GetValue<string>());
            else Assert.Null(request.Content);
            if (id == "GenerateConversation") Assert.Equal("fixture-fresh-1", request.Headers.GetValues("OpenAI-Sentinel-Chat-Requirements-Token").Single());
            return new(HttpStatusCode.OK) { Content = operation.ResponseKind switch
            {
                WebResponseKind.Binary => new ByteArrayContent([1, 2, 3]),
                WebResponseKind.EventStream => new StringContent("data: {\"fixture\":true}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
                _ => new StringContent("{\"fixture\":true}", Encoding.UTF8, "application/json")
            } };
        });
        using var http = new HttpClient(handler);
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture-auth" }), new()
        { BaseUri = new("https://fixture.invalid/"), Endpoints = new() { Conversation = "backend-api/f/conversation" }, SentinelSessionProvider = sentinel });
        JsonObject? body = operation.Method == "POST" ? new() { ["payload"] = "fixture" } : null;
        if (operation.ResponseKind == WebResponseKind.EventStream)
        {
            var events = new List<ServerSentEvent>();
            await foreach (var item in transport.CapturedOperations.StreamAsync("account", id, parameters, query, body)) events.Add(item);
            Assert.Equal(2, events.Count); Assert.Equal("[DONE]", events[^1].Data);
        }
        else if (operation.ResponseKind == WebResponseKind.Binary)
        {
            using var response = await transport.CapturedOperations.SendAsync("account", id, parameters, query, body);
            Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        }
        else Assert.True((await transport.CapturedOperations.SendJsonAsync("account", id, parameters, query, body))!["fixture"]!.GetValue<bool>());
        Assert.Equal(1, calls); Assert.Equal(id == "GenerateConversation" ? 1 : 0, sentinel.Calls);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void Every_operation_rejects_unknown_query_fields_without_including_private_values(string id)
    {
        var operation = WebCapturedOperationsClient.GetOperation(id);
        var error = Assert.Throws<ArgumentException>(() => WebCapturedOperationsClient.GetRequestPath(id, Parameters(operation), new Dictionary<string, string?> { ["unknown"] = "private-fixture-value" }));
        Assert.DoesNotContain("private-fixture-value", error.ToString());
    }
    [Theory]
    [InlineData("GetConversation")]
    [InlineData("GetConversationTurns")]
    [InlineData("DeleteConversation")]
    [InlineData("GetFile")]
    [InlineData("GetFileDownloadInfo")]
    [InlineData("GetConnectorLogo")]
    [InlineData("GetProject")]
    [InlineData("ListProjectConversations")]
    [InlineData("ListProjectSaves")]
    [InlineData("ListProjectConnectorScopes")]
    public void Missing_path_parameters_fail_before_network(string id) =>
        Assert.Throws<ArgumentException>(() => WebCapturedOperationsClient.GetRequestPath(id));
    [Fact]
    public void Catalog_covers_all_service_requests_and_has_no_captured_identifiers()
    {
        var operations = WebCapturedOperationsClient.Operations;
        Assert.Equal(55, operations.Count); Assert.Equal(292, operations.Sum(o => o.CapturedRequests));
        Assert.Equal(55, operations.Select(o => o.Id).Distinct().Count());
        Assert.Equal(2, operations.Count(o => o.ResponseKind == WebResponseKind.EventStream));
        Assert.DoesNotContain(operations, o => o.Path.Contains("file_000000") || o.Path.Contains("asdk_app_"));
        Assert.All(operations, o => Assert.NotEmpty(o.CapturedStatuses));
    }
    [Theory]
    [InlineData("GenerateConversation")]
    [InlineData("ProcessUpload")]
    [InlineData("DownloadEstuaryContent")]
    [InlineData("GetConnectorLogo")]
    public async Task Event_streams_and_binary_operations_cannot_be_misread_as_json(string id)
    {
        using var http = new HttpClient(new RecordingHandler((_, _) => throw new InvalidOperationException("Unexpected network request.")));
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.CapturedOperations.SendJsonAsync("account", id));
    }
    [Fact]
    public async Task Generation_raw_request_cannot_skip_fresh_authorization()
    {
        using var http = new HttpClient();
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await Assert.ThrowsAsync<ArgumentException>(async () => await transport.CapturedOperations.SendAsync("account", "GenerateConversation"));
    }
    [Theory]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.NoContent, "")]
    public async Task An_absent_default_tab_recommendation_is_a_successful_nullable_result(HttpStatusCode status, string content)
    {
        using var http = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") })));
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        Assert.Null(await transport.GetDefaultTabRecommendationAsync("account"));
        Assert.Null(await transport.CapturedOperations.SendJsonAsync("account", "GetDefaultTabRecommendation"));
    }
}
