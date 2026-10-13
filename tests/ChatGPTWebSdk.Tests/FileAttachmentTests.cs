using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Files;
using OpenAI.Responses;

namespace ChatGPTWebSdk.Tests;

public sealed class FileAttachmentTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("private synthetic document");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
    private static readonly ConversationScope Scope = new("account", "alice");
    private static ChatGPTWebRuntime Runtime(FileWebHandler handler) => new(new()
    {
        Credentials = new StaticWebCredentialProvider("account", new() { AccessToken = "synthetic-files", CookieHeader = "synthetic-cookie=value" }),
        AccountId = "account", UserId = "alice", ClientKey = "alice-key", HttpClient = new(handler, false),
        Mode = ChatGPTWebMode.ExternalApi, Endpoints = new(), ConversationStore = new InMemoryConversationStore(),
        Clients = new Dictionary<string, ConversationScope> { ["alice-key"] = Scope, ["bob-key"] = new("account", "bob") }
    });

    [Theory]
    [InlineData("REPORT.PDF", "application/octet-stream", "application/pdf", "my_files")]
    [InlineData("notes.txt", "application/octet-stream", "text/plain", "my_files")]
    [InlineData("table.CSV", "application/octet-stream", "text/csv", "my_files")]
    [InlineData("code.json", "application/octet-stream", "application/json", "my_files")]
    [InlineData("notes.md", "application/octet-stream", "text/markdown", "my_files")]
    [InlineData("document.docx", "application/octet-stream", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "my_files")]
    [InlineData("archive.ZIP", "application/octet-stream", "application/zip", "ace_upload")]
    [InlineData("archive.bin", "application/zip", "application/zip", "ace_upload")]
    [InlineData("custom.unknown", "application/x-custom", "application/x-custom", "my_files")]
    [InlineData("unknown.bin", "application/octet-stream", "application/octet-stream", "my_files")]
    [InlineData("photo.JPG", "application/octet-stream", "image/jpeg", "multimodal")]
    [InlineData("photo.png", "image/jpeg", "image/jpeg", "multimodal")]
    public async Task Upload_uses_the_same_file_category_and_MIME_for_creation_storage_and_processing(string name, string mime, string expectedMime, string useCase)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        var file = await runtime.Web.Transport.UploadFileAsync("account", new MemoryStream(Data), new() { FileName = name, MimeType = mime });
        Assert.Equal(expectedMime, file.MimeType);
        foreach (var body in handler.Creations.Concat(handler.Processing))
        { Assert.Equal(useCase, body["use_case"]!.GetValue<string>()); Assert.Equal(expectedMime, body["mime_type"]!.GetValue<string>()); }
        Assert.Equal(expectedMime, Assert.Single(handler.Uploads).Mime); Assert.Equal(Data, handler.Uploads[0].Bytes);
    }

    [Theory]
    [InlineData("")] [InlineData("invalid")] [InlineData("image/*")] [InlineData("*/*")] [InlineData("text/plain\r\nCookie: synthetic")]
    public async Task Invalid_MIME_values_fail_before_remote_file_creation(string mime)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Web.Transport.UploadFileAsync("account", new MemoryStream(Data), new() { FileName = "notes.txt", MimeType = mime }));
        Assert.Empty(handler.Creations); Assert.Empty(handler.Uploads);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/zip")]
    [InlineData("text/plain")]
    public void Documents_are_metadata_attachments_without_image_pointers_or_dimensions(string mime)
    {
        var message = new WebUploadedFile("file-document", "document", mime, 42).ToMessage("read this");
        Assert.Equal("text", message.Content!["content_type"]!.GetValue<string>());
        Assert.Equal("read this", message.Content["parts"]![0]!.GetValue<string>());
        var attachment = message.Metadata!["attachments"]![0]!;
        Assert.Equal("file-document", attachment["id"]!.GetValue<string>());
        Assert.Null(attachment["width"]); Assert.Null(attachment["height"]);
        Assert.DoesNotContain("image_asset_pointer", message.Content.ToJsonString());
    }

    [Theory]
    [InlineData(false, false, "normal")] [InlineData(false, true, "normal")] [InlineData(true, false, "normal")] [InlineData(true, true, "normal")]
    [InlineData(false, false, "project")] [InlineData(false, true, "project")] [InlineData(true, false, "project")] [InlineData(true, true, "project")]
    [InlineData(false, false, "temporary")] [InlineData(false, true, "temporary")] [InlineData(true, false, "temporary")] [InlineData(true, true, "temporary")]
    public async Task Original_file_parts_work_in_Chat_and_Responses_with_and_without_SSE(bool chat, bool streaming, string context)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        var client = runtime.CreateClient(projectId: context == "project" ? "g-p-files-project" : null, temporaryChat: context == "temporary");
        var text = "";
        if (chat)
        {
            UserChatMessage[] messages = [new(ChatMessageContentPart.CreateTextPart("read document"), ChatMessageContentPart.CreateFilePart(BinaryData.FromBytes(Data), "text/plain", "notes.txt"))];
            var chats = client.GetChatClient("gpt-6");
            if (streaming) await foreach (var update in chats.CompleteChatStreamingAsync(messages)) text += string.Concat(update.ContentUpdate.Select(p => p.Text));
            else text = (await chats.CompleteChatAsync(messages)).Value.Content[0].Text;
        }
        else
        {
            ResponseItem[] input = [ResponseItem.CreateUserMessageItem([ResponseContentPart.CreateInputTextPart("read document"), ResponseContentPart.CreateInputFilePart(BinaryData.FromBytes(Data), "text/plain", "notes.txt")])];
            var responses = client.GetResponsesClient();
            if (streaming) await foreach (var update in responses.CreateResponseStreamingAsync("gpt-6", input)) { if (update is StreamingResponseOutputTextDeltaUpdate delta) text += delta.Delta; }
            else text = (await responses.CreateResponseAsync("gpt-6", input)).Value.GetOutputText();
        }
        Assert.Equal("Hello world", text); Assert.Single(handler.Creations);
        var sent = Assert.Single(handler.Conversations.Turns).Body["messages"]![0]!;
        Assert.Equal("text", sent["content"]!["content_type"]!.GetValue<string>());
        Assert.Equal("file-fixture-1", sent["metadata"]!["attachments"]![0]!["id"]!.GetValue<string>());
        var body = handler.Conversations.Turns[0].Body;
        if (context == "project") Assert.Equal("g-p-files-project", body["gizmo_id"]!.GetValue<string>());
        Assert.Equal(context == "temporary", body["history_and_training_disabled"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task Mixed_files_and_images_preserve_image_pointers_and_document_metadata()
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        await runtime.CreateClient().GetResponsesClient().CreateResponseAsync("gpt-6", [ResponseItem.CreateUserMessageItem([
            ResponseContentPart.CreateInputFilePart(BinaryData.FromBytes(Data), "text/plain", "notes.txt"),
            ResponseContentPart.CreateInputImagePart(new Uri("data:image/png;base64," + Convert.ToBase64String(Png))), ResponseContentPart.CreateInputTextPart("compare")])]);
        var sent = Assert.Single(handler.Conversations.Turns).Body["messages"]![0]!;
        Assert.Equal("multimodal_text", sent["content"]!["content_type"]!.GetValue<string>());
        Assert.Equal(2, sent["metadata"]!["attachments"]!.AsArray().Count);
        Assert.Equal("image_asset_pointer", sent["content"]!["parts"]![0]!["content_type"]!.GetValue<string>());
        Assert.Equal("compare", sent["content"]!["parts"]![1]!.GetValue<string>());
        Assert.Null(sent["metadata"]!["attachments"]![0]!["width"]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Uploaded_file_IDs_are_owned_by_application_user(bool responses)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler); var client = runtime.CreateClient();
        var file = (await client.GetOpenAIFileClient().UploadFileAsync(new MemoryStream(Data), "notes.txt", FileUploadPurpose.UserData)).Value;
        var bob = runtime.CreateClient("bob-key");
        var error = await Assert.ThrowsAsync<ClientResultException>(async () =>
        {
            if (responses) await bob.GetResponsesClient().CreateResponseAsync("gpt-6", [ResponseItem.CreateUserMessageItem([ResponseContentPart.CreateInputFilePart(file.Id)])]);
            else await bob.GetChatClient("gpt-6").CompleteChatAsync([new UserChatMessage(ChatMessageContentPart.CreateFilePart(file.Id))]);
        });
        Assert.Equal(404, error.Status); Assert.Empty(handler.Conversations.Turns);
        await client.GetResponsesClient().CreateResponseAsync("gpt-6", [ResponseItem.CreateUserMessageItem([ResponseContentPart.CreateInputFilePart(file.Id)])]);
        Assert.Single(handler.Creations); Assert.Single(handler.Conversations.Turns);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"file_url\":\"https://untrusted.test/private\"}")]
    [InlineData("{\"filename\":\"notes.txt\",\"file_data\":\"!invalid-base64!\"}")]
    [InlineData("{\"file_id\":\"file-x\",\"filename\":\"notes.txt\",\"file_data\":\"YQ==\"}")]
    [InlineData("{\"file_id\":\"\"}")]
    [InlineData("{\"file_data\":\"YQ==\"}")]
    [InlineData("{\"filename\":\"bad\\nname.txt\",\"file_data\":\"YQ==\"}")]
    [InlineData("{\"filename\":\"notes.txt\",\"file_data\":\"data:text/plain,YQ==\"}")]
    [InlineData("{\"filename\":\"notes.txt\",\"file_data\":\"YQ==\",\"detail\":\"high\"}")]
    [InlineData("{\"filename\":\"notes.txt\",\"file_data\":\"\"}")]
    [InlineData("{\"filename\":\"notes.txt\",\"file_data\":[]}")]
    [InlineData("{\"file_id\":123}")]
    [InlineData("{\"filename\":{},\"file_data\":\"YQ==\"}")]
    public async Task Invalid_later_file_parts_fail_before_any_upload_or_generation(string fields)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        var adapter = new OpenAiWebAdapter(runtime.Web);
        var invalid = JsonNode.Parse(fields)!.AsObject(); invalid["type"] = "input_file";
        var body = new JsonObject { ["model"] = "gpt-6", ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(
            new JsonObject { ["type"] = "input_file", ["filename"] = "valid.txt", ["file_data"] = Convert.ToBase64String(Data) }, invalid) }) };
        var error = await Record.ExceptionAsync(() => adapter.PrepareResponseAsync(Scope, body));
        Assert.True(error is ArgumentException or UnsupportedWebFeatureException);
        Assert.Empty(handler.Creations); Assert.Empty(handler.Conversations.Turns);
    }

    [Fact]
    public async Task File_content_cache_avoids_uploading_resubmitted_history()
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler); var chat = runtime.CreateClient().GetChatClient("gpt-6");
        var first = new UserChatMessage(ChatMessageContentPart.CreateFilePart(BinaryData.FromBytes(Data), "application/octet-stream", "notes.TXT"));
        await chat.CompleteChatAsync([first]);
        await chat.CompleteChatAsync([first, new AssistantChatMessage("Hello world"), new UserChatMessage("continue")]);
        Assert.Single(handler.Creations); Assert.Equal(2, handler.Conversations.Turns.Count);
        Assert.Single(handler.Conversations.Turns[1].Body["messages"]!.AsArray());
    }

    [Theory]
    [InlineData("YQ==", "notes.TXT", "text/plain")]
    [InlineData("data:application/pdf;base64,YQ==", "wrong.txt", "application/pdf")]
    [InlineData("data:application/zip;base64,YQ==", "archive.ZIP", "application/zip")]
    [InlineData("data:text/plain;charset=utf-8;base64,YQ==", "notes.txt", "text/plain")]
    public async Task Inline_data_uses_its_explicit_MIME_or_infers_from_the_filename(string data, string name, string mime)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        var adapter = new OpenAiWebAdapter(runtime.Web);
        await adapter.PrepareResponseAsync(Scope, new() { ["model"] = "gpt-6", ["input"] = new JsonArray(new JsonObject { ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_file", ["filename"] = name, ["file_data"] = data }) }) });
        Assert.Equal(mime, Assert.Single(handler.Creations)["mime_type"]!.GetValue<string>());
        Assert.Equal(new byte[] { 97 }, Assert.Single(handler.Uploads).Bytes);
    }

    [Theory]
    [InlineData("account", "bob")]
    [InlineData("other-account", "alice")]
    public async Task File_references_cannot_cross_account_or_user_boundaries(string account, string user)
    {
        using var handler = new FileWebHandler(); using var runtime = Runtime(handler);
        var adapter = new OpenAiWebAdapter(runtime.Web);
        var file = await adapter.UploadOwnedFileAsync(Scope, new MemoryStream(Data), new() { FileName = "notes.txt" });
        var error = await Assert.ThrowsAsync<SdkException>(() => adapter.GetOwnedFileAsync(new(account, user), file.Id));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode); Assert.Single(handler.Creations);
    }
}
