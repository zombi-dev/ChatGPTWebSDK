using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Capture;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task Sse_handles_comments_CRLF_multiline_fields_and_EOF()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(": keepalive\r\nid: e1\r\nevent: message\r\ndata: {\"x\":\r\ndata: 1}\r\n\r\ndata: [DONE]"));
        var events = new List<ServerSentEvent>();
        await foreach (var item in ServerSentEvents.ReadAsync(stream)) events.Add(item);
        Assert.Equal(2, events.Count);
        Assert.Equal("{\"x\":\n1}", events[0].Data);
        Assert.Equal("message", events[0].Event);
        Assert.Equal("e1", events[1].Id);
        Assert.Null(events[1].Event);
    }
    [Fact]
    public async Task Sse_limits_event_size()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: abcdef\n\n"));
        await Assert.ThrowsAsync<SdkException>(async () => { await foreach (var item in ServerSentEvents.ReadAsync(stream, 5)) { } });
    }
    [Fact]
    public void Full_snapshots_emit_new_text_once()
    {
        var decoder = new WebStreamDecoder();
        var first = decoder.Decode(new(FakeWebHandler.Snapshot("conversation", "assistant", "Hello", false).ToJsonString()));
        var second = decoder.Decode(new(FakeWebHandler.Snapshot("conversation", "assistant", "Hello world", true).ToJsonString()));
        Assert.Equal("Hello", first.Delta);
        Assert.Equal(" world", second.Delta);
        Assert.True(second.Completed);
        Assert.Equal("", decoder.Decode(new("[DONE]")).Delta);
    }
    [Fact]
    public void Patch_protocol_appends_replaces_and_preserves_ids()
    {
        var decoder = new WebStreamDecoder();
        decoder.Decode(new(new JsonObject { ["v"] = FakeWebHandler.Snapshot("conversation", "assistant", "Hi", false) }.ToJsonString()));
        var appended = decoder.Decode(new("""{"p":"/message/content/parts/0","o":"append","v":" there"}"""));
        Assert.Equal(" there", appended.Delta);
        Assert.Equal("Hi there", appended.Text);
        var replacement = decoder.Decode(new("""{"p":"/message/content/parts/0","o":"replace","v":"Goodbye"}"""));
        Assert.True(replacement.IsTextReplacement);
        Assert.Equal("conversation", replacement.ConversationId);
        Assert.Equal("assistant", replacement.MessageId);
        decoder.Decode(new("""{"o":"patch","v":[{"p":"/message/status","o":"replace","v":"finished_successfully"},{"p":"/message/end_turn","o":"replace","v":true}]}"""));
        Assert.True(decoder.Completed);
    }
    [Fact]
    public void Analysis_and_tool_messages_do_not_leak_into_final_text()
    {
        var decoder = new WebStreamDecoder();
        var analysis = FakeWebHandler.Snapshot("conversation", "analysis", "private reasoning", false);
        analysis["message"]!["channel"] = "analysis";
        Assert.Equal("", decoder.Decode(new(analysis.ToJsonString())).Text);
        Assert.Equal("final answer", decoder.Decode(new(FakeWebHandler.Snapshot("conversation", "final", "final answer", true).ToJsonString())).Delta);
    }
    [Fact]
    public void Generation_error_and_malformed_event_fail_explicitly()
    {
        var decoder = new WebStreamDecoder();
        Assert.Throws<SdkException>(() => decoder.Decode(new("""{"error":{"message":"failure"}}""")));
        Assert.Throws<SdkException>(() => decoder.Decode(new("not-json")));
    }
    [Fact]
    public async Task Har_import_ignores_untrusted_origins_prompts_and_transient_challenges()
    {
        var har = """
        {"log":{"entries":[
          {"request":{"method":"POST","url":"https://chatgpt.com/backend-api/f/conversation","headers":[{"name":"Authorization","value":"Bearer fixture-auth"},{"name":"Cookie","value":"session=fixture"},{"name":"oai-device-id","value":"fixture-device"},{"name":"OpenAI-Sentinel-Proof-Token","value":"transient-proof"}],"postData":{"text":"{\"messages\":[{\"content\":{\"parts\":[\"private prompt\"]}}],\"timezone_offset_min\":-60,\"parent_message_id\":\"parent\",\"model\":\"captured-model\"}"}},"response":{"status":200,"content":{"mimeType":"text/event-stream","text":"data: [DONE]"}}},
          {"request":{"method":"GET","url":"https://attacker.invalid/backend-api/models","headers":[{"name":"Authorization","value":"Bearer stolen"}]},"response":{"status":200}},
          {"request":{"method":"POST","url":"https://chatgpt.com/backend-api/f/conversation","headers":[],"postData":{"text":"{\"conversation_id\":\"conversation\",\"messages\":[]}"}},"response":{"status":200}}
        ]}}
        """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(har));
        var import = await HarCapture.ImportAsync(stream);
        Assert.Equal("fixture-auth", import.Credentials.AccessToken);
        Assert.Equal("session=fixture", import.Credentials.CookieHeader);
        Assert.Equal("backend-api/f/conversation", import.Endpoints.Conversation);
        Assert.Equal("fixture-device", import.Credentials.Headers["oai-device-id"]);
        Assert.DoesNotContain(import.Credentials.Headers, h => h.Key.Contains("Sentinel"));
        Assert.DoesNotContain("private prompt", import.Endpoints.ConversationDefaults.ToJsonString());
        Assert.Null(import.Endpoints.ConversationDefaults["parent_message_id"]);
        Assert.True(import.Report.HasFirstTurn);
        Assert.True(import.Report.HasContinuation);
        Assert.Equal(2, import.Report.Entries.Count);
        Assert.DoesNotContain("fixture-auth", import.ToString());
    }
    [Fact]
    public async Task Cookie_import_filters_other_domains()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""[{"name":"session","value":"fixture","domain":".chatgpt.com"},{"name":"unrelated","value":"no","domain":"other.invalid"}]"""));
        var auth = await HarCapture.ImportCookiesAsync(stream);
        Assert.Equal("session=fixture", auth.CookieHeader);
        Assert.DoesNotContain("fixture", auth.ToString());
    }
    [Theory]
    [InlineData("//attacker.invalid/path")]
    [InlineData("https://attacker.invalid/path")]
    [InlineData("backend-api\\unsafe")]
    public async Task Raw_web_calls_cannot_send_credentials_to_another_origin(string path)
    {
        using var handler = new FakeWebHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Client().Transport.SendJsonAsync("account", HttpMethod.Get, path));
    }
    [Fact]
    public async Task Expired_token_refresh_uses_session_cookies_without_a_browser()
    {
        string? auth = null, cookie = null;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/session")
            {
                cookie = request.Headers.GetValues("Cookie").Single();
                Assert.Null(request.Headers.Authorization);
                return FakeWebHandler.Json(new JsonObject { ["accessToken"] = "fresh-fixture", ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
            }
            auth = request.Headers.Authorization?.Parameter;
            await Task.CompletedTask;
            return FakeWebHandler.Json(new JsonObject { ["models"] = new JsonArray() });
        });
        using var http = new HttpClient(handler);
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", new() { AccessToken = "expired", CookieHeader = "session=fixture", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        await transport.GetModelsAsync("account");
        Assert.Equal("fresh-fixture", auth);
        Assert.Equal("session=fixture", cookie);
    }
}

