using System.Net;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OAuth;
using ChatGPTWebSdk.Protocol;
using OpenAI;
using OpenAI.Images;
using Xunit;
using static ChatGPTWebSdk.Tests.OAuthAuthenticationTests;

namespace ChatGPTWebSdk.Tests;

public sealed class CodexOAuthImageTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OfficialImageClientsUseDirectCodexGenerationAndEditing(bool edit)
    {
        var imageBytes = new byte[] { 137, 80, 78, 71 };
        using var upstream = new Handler(async (request, ct) =>
        {
            Assert.Equal("/backend-api/codex/images/" + (edit ? "edits" : "generations"), request.RequestUri!.AbsolutePath);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!;
            Assert.Equal("gpt-image-2", body["model"]!.GetValue<string>()); Assert.Equal("a blue square", body["prompt"]!.GetValue<string>());
            if (edit) Assert.Equal("data:image/png;base64," + Convert.ToBase64String(imageBytes), body["images"]![0]!["image_url"]!.GetValue<string>());
            return Json(new() { ["created"] = 1, ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(imageBytes) }) });
        });
        using var http = new HttpClient(upstream, false); using var runtime = new ChatGPTCodex(new StaticOAuthSessionProvider(Session()), upstream:http);
        var client = runtime.CreateClient().GetImageClient("gpt-image-2");
        var result = edit ? await client.GenerateImageEditAsync(new MemoryStream(imageBytes), "image.png", "a blue square", new ImageEditOptions { ResponseFormat = GeneratedImageFormat.Bytes })
            : await client.GenerateImageAsync("a blue square", new ImageGenerationOptions { ResponseFormat = GeneratedImageFormat.Bytes });
        Assert.Equal(imageBytes, result.Value.ImageBytes.ToArray()); Assert.Equal(1, upstream.Calls);
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("input_fidelity")]
    [InlineData("moderation")]
    [InlineData("output_compression")]
    [InlineData("output_format")]
    [InlineData("partial_images")]
    [InlineData("stream")]
    [InlineData("response_format")]
    public async Task UnsupportedControlsAreRejectedBeforeUpstream(string option)
    {
        var body = new JsonObject { ["prompt"] = "a square", [option] = option == "stream" ? JsonValue.Create(true) : JsonValue.Create("unsupported") };
        using var content = new StringContent(body.ToJsonString());
        await Assert.ThrowsAsync<SdkException>(() => CodexImages.PrepareAsync(content, false, default));
    }

    [Theory]
    [InlineData(0)] [InlineData(6)]
    public async Task EditingRequiresOneToFiveReferences(int count)
    {
        using var content = new MultipartFormDataContent(); content.Add(new StringContent("a square"), "prompt");
        for (var i = 0; i < count; i++) content.Add(new ByteArrayContent([1,2,3]), "image[]", "image.png");
        await Assert.ThrowsAsync<ArgumentException>(() => CodexImages.PrepareAsync(content, true, default));
    }

    [Fact]
    public async Task MultipartBinaryAndSeveralReferencesArePreserved()
    {
        using var content = new MultipartFormDataContent(); content.Add(new StringContent("blend them"), "prompt"); content.Add(new StringContent("2"), "n");
        for (var i = 0; i < 5; i++) content.Add(new ByteArrayContent([0,255,(byte)i,13,10]), "image[]", "image.png");
        var body = await CodexImages.PrepareAsync(content, true, default); Assert.Equal(5, body["images"]!.AsArray().Count); Assert.Equal(2, body["n"]!.GetValue<int>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String([0,255,4,13,10]), body["images"]![4]!["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedMultipartFailsLocally()
    {
        using var content = new StringContent("not multipart");
        await Assert.ThrowsAsync<ArgumentException>(() => CodexImages.PrepareAsync(content, true, default));
    }
}
