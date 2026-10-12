using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;

namespace ChatGPTWebSdk.Tests;

public sealed class OfficialBackendTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Drop_in_clients_can_call_the_official_API_alongside_configured_web_clients(bool streaming)
    {
        using var webHandler = new FakeWebHandler(); using var webHttp = new HttpClient(webHandler, false);
        await using var web = ChatGPTWeb.Initialize(new ChatGPTWebRuntimeOptions { Mode = ChatGPTWebMode.ExternalApi, Credentials = new StaticWebCredentialProvider("default", new()), HttpClient = webHttp, ConversationStore = new InMemoryConversationStore() });
        using var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-official-key", request.Headers.Authorization!.Parameter);
            var body = "{\"id\":\"chatcmpl_official\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Official\"}}]}";
            if (streaming) body = "data: " + body.Replace("\"message\":", "\"delta\":") + "\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") });
        });
        using var http = new HttpClient(handler);
        var client = ChatGPTOfficial.CreateClient("synthetic-official-key", new() { Transport = new HttpClientPipelineTransport(http) }).GetChatClient("test-model");
        var text = "";
        if (streaming) await foreach (var update in client.CompleteChatStreamingAsync("Hello")) text += string.Concat(update.ContentUpdate.Select(p => p.Text));
        else text = (await client.CompleteChatAsync("Hello")).Value.Content[0].Text;
        Assert.Equal("Official", text); Assert.Empty(webHandler.Turns);
        Assert.NotNull(ChatGPTOfficial.CreateClient("synthetic-key"));
    }
}
