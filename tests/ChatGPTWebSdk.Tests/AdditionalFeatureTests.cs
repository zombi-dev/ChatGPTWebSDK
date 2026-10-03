using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class AdditionalFeatureTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(null, null)]
    public async Task Archived_and_starred_filters_preserve_false_and_omit_unset_values(bool? flag, string? expected)
    {
        using var handler = new RecordingHandler((request, _) =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("offset=0", query); Assert.Contains("limit=20", query);
            if (expected is null) { Assert.DoesNotContain("is_archived", query); Assert.DoesNotContain("is_starred", query); Assert.DoesNotContain("expand", query); }
            else { Assert.Contains("is_archived=" + expected, query); Assert.Contains("is_starred=" + expected, query); Assert.Contains("expand=" + expected, query); }
            Assert.Contains("conversation_origin=fixture%20%26%20origin", query);
            return Task.FromResult(FakeWebHandler.Json(new JsonObject()));
        });
        using var http = new HttpClient(handler);
        var api = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await api.ListConversationsAsync("account", new WebConversationQuery { Archived = flag, Starred = flag, Expand = flag, ConversationOrigin = "fixture & origin" });
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Model_filters_and_file_download_flags_are_encoded_as_nullable_booleans(bool? flag)
    {
        var calls = 0;
        using var handler = new RecordingHandler((request, _) =>
        {
            calls++;
            var query = request.RequestUri!.Query;
            if (flag is { } actual) Assert.Contains("=" + actual.ToString().ToLowerInvariant(), query);
            else Assert.Equal("", query);
            return Task.FromResult(FakeWebHandler.Json(new JsonObject()));
        });
        using var http = new HttpClient(handler);
        var api = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await api.GetModelsAsync("account", new WebModelQuery { IncludeIcons = flag, IsGizmo = flag, IncludeInactiveModels = flag });
        await api.GetFileDownloadInfoAsync("account", "file_fixture", new WebFileQuery { Inline = flag, IncludeLibraryFileState = flag });
        Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task Invalid_pagination_for_new_features_fails_without_network(int limit)
    {
        using var http = new HttpClient(new RecordingHandler((_, _) => throw new InvalidOperationException("Unexpected network.")));
        var api = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await api.ListNotificationsAsync("account", limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await api.ListInstalledPluginsAsync("account", limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await api.ListCodexTasksAsync("account", limit));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upload_preserves_the_captured_library_and_entry_surface_controls(bool store)
    {
        var jsonRequests = 0;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
                Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content!.ReadAsByteArrayAsync(ct));
                return new(HttpStatusCode.Created);
            }
            jsonRequests++;
            var body = (await request.Content!.ReadFromJsonAsync<JsonObject>(ct))!;
            Assert.Equal("chat_composer", body["entry_surface"]!.GetValue<string>());
            Assert.Equal("opportunistic", body["library_persistence_mode"]!.GetValue<string>());
            if (request.RequestUri!.AbsolutePath == "/backend-api/files")
            {
                Assert.Equal(store, body["store_in_library"]!.GetValue<bool>());
                return FakeWebHandler.Json(new JsonObject { ["file_id"] = "file_fixture", ["upload_url"] = "https://files.openai.com/upload" });
            }
            Assert.Equal(store, body["metadata"]!["store_in_library"]!.GetValue<bool>());
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"event\":\"file.processing.completed\"}\n\n", Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var api = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "fixture" }));
        var file = await api.UploadFileAsync("account", new MemoryStream([1, 2, 3]), new() { FileName = "fixture.bin", StoreInLibrary = store, EntrySurface = "chat_composer", LibraryPersistenceMode = "opportunistic" });
        Assert.Equal("file_fixture", file.Id); Assert.Equal(2, jsonRequests);
    }
}
