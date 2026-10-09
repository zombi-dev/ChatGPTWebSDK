using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class DropInTests
{
    private static ChatGPTWebRuntime Runtime(FakeWebHandler handler) => new(new()
    {
        Mode = ChatGPTWebMode.ApiOnly, HttpClient = new HttpClient(handler, false),
        Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic-token" }),
        AccountId = "account", UserId = "alice", ClientKey = "synthetic-app-key", ConversationStore = new InMemoryConversationStore(), Endpoints = new()
    });
    [Fact]
    public async Task Official_ChatClient_async_and_sync_accept_native_completion_types_and_append_only()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetChatClient("gpt-6");
        ChatCompletion first = await client.CompleteChatAsync("first");
        Assert.Equal("Hello world", first.Content[0].Text);
        ChatCompletion second = client.CompleteChat("second");
        Assert.Equal("Hello world", second.Content[0].Text);
        Assert.Equal("conversation-1", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.Single(handler.Turns[1].Body["messages"]!.AsArray());
        Assert.Equal("assistant-1", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Official_response_types_and_previous_response_id_continue_the_same_remote_chat()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetResponsesClient();
        ResponseResult first = await client.CreateResponseAsync("gpt-6", "first");
        Assert.Equal("Hello world", first.GetOutputText());
        ResponseResult second = await client.CreateResponseAsync("gpt-6", "second", first.Id);
        Assert.Equal("Hello world", second.GetOutputText());
        Assert.Equal("conversation-1", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Official_chat_streaming_parser_accepts_the_virtual_endpoint()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetChatClient("gpt-6"); var text = "";
        await foreach (var update in client.CompleteChatStreamingAsync("hello")) foreach (var part in update.ContentUpdate) text += part.Text;
        Assert.Equal("Hello world", text);
    }
    [Fact]
    public async Task Official_responses_streaming_parser_accepts_the_virtual_endpoint()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetResponsesClient(); var text = "";
        await foreach (var update in client.CreateResponseStreamingAsync("gpt-6", "hello"))
            if (update is StreamingResponseOutputTextDeltaUpdate delta) text += delta.Delta;
        Assert.Equal("Hello world", text);
    }
    [Fact]
    public async Task Unsupported_official_controls_keep_501_and_never_send_a_turn()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetChatClient("gpt-6");
        var error = await Assert.ThrowsAsync<ClientResultException>(() => client.CompleteChatAsync([new UserChatMessage("hello")], new() { Temperature = 0.5f }));
        Assert.Equal(501, error.Status); Assert.Empty(handler.Turns);
    }
    [Fact]
    public void Browser_only_mode_fails_at_initialization()
    {
        Assert.Throws<NotSupportedException>(() => new ChatGPTWebRuntime(new() { Mode = ChatGPTWebMode.BrowserOnly, Credentials = new StaticWebCredentialProvider("account", new()) }));
    }
    [Fact]
    public async Task RealTime_is_guarded_before_a_socket_or_paid_API_can_open()
    {
        using var handler = new FakeWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient().GetRealtimeClient();
        await Assert.ThrowsAsync<ChatGPTWebSdk.Protocol.UnsupportedWebFeatureException>(() => client.StartConversationSessionAsync("gpt-6"));
        Assert.Empty(handler.Turns);
    }
}
