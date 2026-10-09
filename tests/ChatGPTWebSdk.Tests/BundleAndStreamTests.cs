using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class BundleAndStreamTests
{
    [Fact]
    public void Packaged_browser_is_resolved_relative_to_its_manifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-bundle-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "engine"));
            File.WriteAllText(Path.Combine(directory, "engine", "browser"), "synthetic executable");
            File.WriteAllText(Path.Combine(directory, "browser.json"), new JsonObject { ["version"] = 1, ["platform"] = BundledBrowser.Platform, ["relativeExecutable"] = "engine/browser" }.ToJsonString());
            Assert.Equal(Path.Combine(directory, "engine", "browser"), BundledBrowser.FindExecutable(directory));
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("engine/../../escape")]
    [InlineData("")]
    [InlineData("wrong-platform")]
    [InlineData("invalid-json")]
    [InlineData("wrong-version")]
    [InlineData("missing-manifest")]
    public void Invalid_browser_bundles_fail_with_a_repairable_error(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-bundle-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(directory);
            var manifest = new JsonObject { ["version"] = kind == "wrong-version" ? 2 : 1, ["platform"] = kind == "wrong-platform" ? "unavailable-platform" : BundledBrowser.Platform, ["relativeExecutable"] = kind };
            if (kind != "missing-manifest") File.WriteAllText(Path.Combine(directory, "browser.json"), kind == "invalid-json" ? "invalid" : manifest.ToJsonString());
            Assert.Equal("sentinel_browser_unavailable", Assert.Throws<SdkException>(() => BundledBrowser.FindExecutable(directory)).Code);
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Theory]
    [InlineData(1, "ASCII")]
    [InlineData(1, "emoji 😀 split")]
    [InlineData(1, "日本語")]
    [InlineData(2, "emoji 😀 split")]
    [InlineData(3, "日本語")]
    [InlineData(7, "line\nnext line")]
    [InlineData(64, "line\nnext line")]
    [InlineData(4096, "emoji 😀 split")]
    public async Task SSE_decoding_survives_fragmented_UTF8_data(int chunk, string text)
    {
        var data = "id: synthetic-id\r\nevent: fixture\r\n" + string.Join("\r\n", text.Split('\n').Select(line => "data: " + line)) + "\r\n\r\ndata: [DONE]\r\n\r\n";
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(data), chunk);
        var events = new List<ServerSentEvent>();
        await foreach (var item in ServerSentEvents.ReadAsync(stream)) events.Add(item);
        Assert.Equal(2, events.Count); Assert.Equal(text, events[0].Data); Assert.Equal("fixture", events[0].Event); Assert.Equal("synthetic-id", events[0].Id); Assert.Equal("[DONE]", events[1].Data);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Temporary_threads_restart_with_fresh_remote_conversations_and_keep_the_mode(bool compatible)
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-temporary-restart-" + Guid.NewGuid());
        try
        {
            using var fake = new FakeWebHandler(); var scope = new ConversationScope("account", "alice", "temporary");
            var first = fake.Client(new FileConversationStore(directory));
            var result = await first.SendAsync(scope, new() { Model = "gpt-6", Messages = [WebInputMessage.User("private temporary input")], TemporaryChat = true });
            var restarted = fake.Client(new FileConversationStore(directory));
            Assert.True((await restarted.GetStateAsync(scope)).TemporaryChat);
            Assert.Null((await restarted.GetStateAsync(scope)).ConversationId);
            var next = compatible
                ? await new Compatibility.OpenAiWebAdapter(restarted).PrepareResponseAsync(scope, new() { ["model"] = "gpt-6", ["input"] = "new input", ["store"] = false })
                : new WebTurnRequest { Model = "gpt-6", Messages = [WebInputMessage.User("new input")], TemporaryChat = true };
            await restarted.SendAsync(scope, next);
            Assert.Null(fake.Turns[1].Body["conversation_id"]);
            Assert.False(fake.Turns[1].Body["temporary_chat_requests_personalization"]!.GetValue<bool>());
            await Assert.ThrowsAsync<SdkException>(() => restarted.GetResponseAsync(scope, result.Response.Id));
            await Assert.ThrowsAsync<SdkException>(() => restarted.ValidateContextAsync(scope, new()));
        }
        finally { TestDirectory.Delete(directory); }
    }

    private sealed class FragmentedStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => base.ReadAsync(buffer, offset, Math.Min(count, chunk), ct);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Already_absent_temporary_chats_can_be_cleared_but_persistent_deletion_errors_are_preserved(bool temporary)
    {
        using var fake = new FakeWebHandler(); using var invoker = new HttpMessageInvoker(fake, false);
        using var http = new HttpClient(new RecordingHandler((request, ct) => request.Method == HttpMethod.Delete
            ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)) : invoker.SendAsync(request, ct)));
        var client = new ChatGptWebClient(new(http, new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic" })), new InMemoryConversationStore());
        var scope = new ConversationScope("account", "alice");
        await client.SendAsync(scope, new() { Model = "gpt-6", Messages = [WebInputMessage.User("hello")], TemporaryChat = temporary });
        if (temporary)
        {
            await client.UpdateConversationAsync(scope, delete: true);
            Assert.Null((await client.GetStateAsync(scope)).ConversationId); Assert.Empty((await client.GetStateAsync(scope)).History);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SdkException>(() => client.UpdateConversationAsync(scope, delete: true));
            Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode); Assert.NotNull((await client.GetStateAsync(scope)).ConversationId);
        }
    }
}
