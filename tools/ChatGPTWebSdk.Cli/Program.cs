using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Capture;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    if (args.Length == 2 && args[0] == "browser-check")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var settings = Browser(config);
        var credentials = config["accounts"]![0]!["credentials"]!.Deserialize<WebCredentials>(json)!;
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        using var startup = new CancellationTokenSource(settings.Timeout);
        await using var owned = await ChatGPTWebSdk.Browser.OwnedBrowser.LaunchAsync(playwright, settings, startup.Token);
        var browser = owned.Browser;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Disconnected += (_, _) => closed.TrySetResult();
        var context = await browser.NewContextAsync(new() { ServiceWorkers = Microsoft.Playwright.ServiceWorkerPolicy.Block, ViewportSize = Microsoft.Playwright.ViewportSize.NoViewport });
        context.Page += (_, newPage) => newPage.Close += (_, _) => { if (context.Pages.All(p => p.IsClosed)) closed.TrySetResult(); };
        if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
            await context.AddCookiesAsync(credentials.CookieHeader.Split(';').Select(pair => pair.Trim().Split('=', 2)).Where(pair => pair.Length == 2)
                .Select(pair => new Microsoft.Playwright.Cookie { Name = pair[0], Value = pair[1], Url = "https://chatgpt.com/", Secure = true }));
        var page = await context.NewPageAsync();
        await page.BringToFrontAsync();
        Console.WriteLine("Diagnostic browser is open with the SDK's launch settings. It will remain open until you close its windows. No SDK turn is submitted.");
        try { await page.GotoAsync("https://chatgpt.com/", new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded, Timeout = 30000 }); }
        catch (Microsoft.Playwright.PlaywrightException) { Console.WriteLine("Initial navigation did not complete. The browser remains open for manual checks."); }
        if (!page.IsClosed)
            Console.WriteLine(await page.EvaluateAsync<string>("() => JSON.stringify({ webdriver: navigator.webdriver, userAgent: navigator.userAgent, brands: navigator.userAgentData?.brands, viewport: { width: innerWidth, height: innerHeight }, window: { width: outerWidth, height: outerHeight } })"));
        await closed.Task;
        Console.WriteLine("Diagnostic browser was closed.");
        return 0;
    }
    if (args.Length == 2 && args[0] == "inspect-har")
    {
        await using var capture = File.OpenRead(args[1]);
        var import = await HarCapture.ImportAsync(capture);
        Console.WriteLine(JsonSerializer.Serialize(import.Report, json));
        Console.WriteLine($"Access token present: {!string.IsNullOrWhiteSpace(import.Credentials.AccessToken)}; cookies present: {!string.IsNullOrWhiteSpace(import.Credentials.CookieHeader)}");
        return 0;
    }
    if (args.Length == 3 && args[0] == "import-har")
    {
        await using var capture = File.OpenRead(args[1]);
        var import = await HarCapture.ImportAsync(capture);
        var output = Path.GetFullPath(args[2]);
        if (File.Exists(output)) throw new IOException("Output already exists. Choose a new file to preserve existing configuration.");
        var configuration = new
        {
            baseUri = "https://chatgpt.com/", profile = import.Endpoints, mode = "hybrid", httpDriver = "systemCurl",
            sessionDirectory = Path.Combine(Path.GetDirectoryName(output)!, ".sessions"),
            accounts = new[] { new { id = "default", credentials = import.Credentials } },
            clients = new[] { new { apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), accountId = "default", userId = "default" } },
            modelAliases = new Dictionary<string, string>()
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(configuration, json));
        Console.WriteLine($"Imported configuration to {output}. The proxy API key and session credentials are stored there; no requests were replayed.");
        Console.WriteLine(JsonSerializer.Serialize(import.Report, json));
        return 0;
    }
    if (args.Length >= 2 && args[0] == "smoke")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        var credentials = account["credentials"]!.Deserialize<WebCredentials>(json)!;
        var profile = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? new();
        var mode = Mode(config);
        if (mode == global::OpenAI.ChatGPTWebMode.BrowserOnly) throw new NotSupportedException("Browser-only mode is reserved and is not implemented.");
        using var http = DiagnosticHttp(config);
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), credentials),
            new() { BaseUri = new(config["baseUri"]?.GetValue<string>() ?? "https://chatgpt.com/"), Endpoints = profile,
                SentinelSessionProvider = mode == global::OpenAI.ChatGPTWebMode.Hybrid ? new ChatGPTWebSdk.Browser.BrowserSentinelProvider(Browser(config)) : null });
        var models = await transport.GetModelsAsync(account["id"]!.GetValue<string>());
        Console.WriteLine("Authenticated models request succeeded.");
        if (models["models"] is JsonArray modelList) Console.WriteLine("Available model slugs: " + string.Join(", ", modelList.Select(m => m!["slug"]?.GetValue<string>())));
        if (args.Length == 4 && args[2] == "--send")
        {
            var scope = new ConversationScope(account["id"]!.GetValue<string>(), "sdk-smoke", Guid.NewGuid().ToString("N"));
            var client = new ChatGptWebClient(transport, new FileConversationStore(config["sessionDirectory"]?.GetValue<string>() ?? ".sessions"));
            var nonce = Guid.NewGuid().ToString("N");
            var first = await client.SendAsync(scope, $"Remember this exact marker for the next message: {nonce}. Reply OK.", args[3]);
            var second = await client.SendAsync(scope, "What exact marker did I give you in my previous message? Reply with the marker only.", args[3]);
            if (first.Response.ConversationId != second.Response.ConversationId || !second.Text.Contains(nonce, StringComparison.Ordinal))
                throw new SdkException("Live continuation did not preserve the test marker.", "live_continuation_failed");
            // Independent remote read proves both generated nodes exist on the linked conversation.
            var remote = await transport.GetConversationAsync(scope.AccountId, second.Response.ConversationId);
            if (remote["mapping"] is not JsonObject mapping || !mapping.Any(n => n.Value?["message"]?["id"]?.GetValue<string>() == first.Response.MessageId) ||
                !mapping.Any(n => n.Value?["message"]?["id"]?.GetValue<string>() == second.Response.MessageId))
                throw new SdkException("The remote conversation did not contain both generated messages.", "live_remote_verification_failed");
            Console.WriteLine($"Live two-turn continuation and independent remote history read succeeded: {second.ConversationUrl}");
            Console.WriteLine("The smoke conversation is retained for review.");
        }
        else if (args.Length != 2) throw new ArgumentException("Usage: smoke <config.session.json> [--send <web-model-slug>]");
        return 0;
    }
    if (args.Length >= 2 && args[0] == "smoke-sdk")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        var thread = "sdk-smoke-" + Guid.NewGuid().ToString("N");
        using var http = DiagnosticHttp(config);
        using var runtime = global::OpenAI.ChatGPTWeb.Initialize(new()
        {
            Credentials = new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), account["credentials"]!.Deserialize<WebCredentials>(json)!),
            AccountId = account["id"]!.GetValue<string>(), UserId = "sdk-smoke", SessionDirectory = config["sessionDirectory"]?.GetValue<string>() ?? ".sessions",
            Endpoints = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? WebEndpointProfile.Modern,
            Mode = Mode(config), HttpClient = http, Browser = Browser(config), RequestTimeout = BrowserRequestTimeout(config)
        });
        var models = await runtime.CreateClient().GetOpenAIModelClient().GetModelsAsync();
        Console.WriteLine("Official OpenAIModelClient deserialized the authenticated web model list successfully.");
        if (args.Length == 4 && args[2] == "--send")
        {
            var client = runtime.CreateClient(threadId: thread).GetChatClient(args[3]);
            var marker = Guid.NewGuid().ToString("N");
            var first = (await client.CompleteChatAsync("Remember this exact marker: " + marker + ". Reply OK.")).Value;
            var second = (await client.CompleteChatAsync("What exact marker did I give you previously? Reply with the marker only.")).Value;
            if (!string.Concat(second.Content.Select(c => c.Text)).Contains(marker, StringComparison.Ordinal)) throw new SdkException("Live SDK continuation did not remember the marker.", "live_continuation_failed");
            var scope = runtime.ResolveScope(runtime.ClientKey, thread);
            var state = await runtime.Web.GetStateAsync(scope);
            var remote = await runtime.Web.Transport.GetConversationAsync(scope.AccountId, state.ConversationId!);
            if (remote["mapping"] is not JsonObject mapping || !state.History.All(m => mapping.Any(n => n.Value?["message"]?["id"]?.GetValue<string>() == m.Id)))
                throw new SdkException("Independent remote read did not confirm every SDK message.", "live_remote_verification_failed");
            Console.WriteLine("Live official ChatClient: two appended turns remembered the marker, and an independent HTTP read confirmed the remote messages.");
            Console.WriteLine("Review URL: https://chatgpt.com/c/" + state.ConversationId);
        }
        else if (args.Length != 2) throw new ArgumentException("Usage: smoke-sdk <config.session.json> [--send <web-model-slug>]");
        return 0;
    }
    if (args.Length is 4 or 5 && args[0] == "inspect-sdk")
    {
        if (!args[2].StartsWith("sdk-", StringComparison.Ordinal)) throw new ArgumentException("Select an SDK test thread.");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        using var http = DiagnosticHttp(config);
        using var runtime = global::OpenAI.ChatGPTWeb.Initialize(new()
        {
            Credentials = new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), account["credentials"]!.Deserialize<WebCredentials>(json)!),
            AccountId = account["id"]!.GetValue<string>(), UserId = "sdk-smoke", SessionDirectory = config["sessionDirectory"]!.GetValue<string>(),
            Endpoints = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? WebEndpointProfile.Modern,
            Mode = global::OpenAI.ChatGPTWebMode.ApiOnly, HttpClient = http
        });
        var scope = runtime.ResolveScope(runtime.ClientKey, args[2]);
        var state = await runtime.Web.GetStateAsync(scope);
        if (state.ConversationId is null) throw new ArgumentException("This SDK thread has no known remote conversation.");
        var remote = await runtime.Web.Transport.GetConversationAsync(scope.AccountId, state.ConversationId);
        await File.WriteAllTextAsync(args[3], remote.ToJsonString(json));
        Console.WriteLine("Private remote snapshot saved to the chosen local path. Conversation: https://chatgpt.com/c/" + state.ConversationId);
        if (args.Length == 5 && args[4] == "--reconcile")
        {
            await runtime.Web.ReconcileAsync(scope);
            state = await runtime.Web.GetStateAsync(scope);
            Console.WriteLine("Reconciliation succeeded. Recorded messages: " + state.History.Count);
        }
        return 0;
    }
    if (args.Length == 4 && args[0] == "resume-sdk")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        using var http = DiagnosticHttp(config);
        using var runtime = global::OpenAI.ChatGPTWeb.Initialize(new()
        {
            Credentials = new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), account["credentials"]!.Deserialize<WebCredentials>(json)!),
            AccountId = account["id"]!.GetValue<string>(), UserId = "sdk-smoke", SessionDirectory = config["sessionDirectory"]!.GetValue<string>(),
            Endpoints = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? WebEndpointProfile.Modern,
            Mode = Mode(config), HttpClient = http, Browser = Browser(config), RequestTimeout = BrowserRequestTimeout(config)
        });
        var scope = runtime.ResolveScope(runtime.ClientKey, args[2]);
        var state = await runtime.Web.GetStateAsync(scope);
        var marker = System.Text.RegularExpressions.Regex.Match(state.History.FirstOrDefault()?.Text ?? "", "(?<=marker: )[0-9a-f]{32}").Value;
        if (state.ConversationId is null || marker.Length != 32 || state.RequiresReconciliation) throw new ArgumentException("This thread is not a confirmed SDK smoke conversation.");
        var result = await runtime.CreateClient(threadId: args[2]).GetChatClient(args[3]).CompleteChatAsync("What exact marker did I give you previously? Reply with the marker only.");
        if (!string.Concat(result.Value.Content.Select(c => c.Text)).Contains(marker, StringComparison.Ordinal)) throw new SdkException("Restart continuation did not preserve the marker.", "live_restart_continuation_failed");
        state = await runtime.Web.GetStateAsync(scope);
        var remote = await runtime.Web.Transport.GetConversationAsync(scope.AccountId, state.ConversationId!);
        if (remote["mapping"] is not JsonObject mapping || !state.History.All(m => mapping.Any(n => n.Value?["message"]?["id"]?.GetValue<string>() == m.Id)))
            throw new SdkException("Independent remote history check failed after restart.", "live_remote_verification_failed");
        Console.WriteLine("Official ChatClient restart continuation remembered the marker, and every local message was confirmed in the remote conversation graph.");
        Console.WriteLine("Review URL: https://chatgpt.com/c/" + state.ConversationId);
        return 0;
    }
    if (args.Length == 4 && args[0] == "manage-sdk")
    {
        if (!args[2].StartsWith("sdk-edit-regenerate-", StringComparison.Ordinal)) throw new ArgumentException("Select an existing disposable SDK management thread.");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        using var http = DiagnosticHttp(config);
        using var runtime = global::OpenAI.ChatGPTWeb.Initialize(new()
        {
            Credentials = new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), account["credentials"]!.Deserialize<WebCredentials>(json)!),
            AccountId = account["id"]!.GetValue<string>(), UserId = "sdk-smoke", SessionDirectory = config["sessionDirectory"]!.GetValue<string>(),
            Endpoints = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? WebEndpointProfile.Modern,
            Mode = Mode(config), HttpClient = http, Browser = Browser(config), RequestTimeout = BrowserRequestTimeout(config)
        });
        await VerifyManagement(runtime, runtime.ResolveScope(runtime.ClientKey, args[2]), args[3]);
        return 0;
    }
    if (args.Length == 4 && args[0] is "smoke-feature" or "smoke-features")
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!;
        var account = config["accounts"]![0]!;
        using var http = DiagnosticHttp(config);
        using var runtime = global::OpenAI.ChatGPTWeb.Initialize(new()
        {
            Credentials = new StaticWebCredentialProvider(account["id"]!.GetValue<string>(), account["credentials"]!.Deserialize<WebCredentials>(json)!),
            AccountId = account["id"]!.GetValue<string>(), UserId = "sdk-smoke", SessionDirectory = config["sessionDirectory"]!.GetValue<string>(),
            Endpoints = config["profile"]?.Deserialize<WebEndpointProfile>(json) ?? WebEndpointProfile.Modern,
            Mode = Mode(config), HttpClient = http, Browser = Browser(config), RequestTimeout = BrowserRequestTimeout(config)
        });
        foreach (var feature in args[0] == "smoke-features" ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries) : [args[3]])
        {
        var thread = "sdk-" + feature + "-" + Guid.NewGuid().ToString("N");
        var scope = runtime.ResolveScope(runtime.ClientKey, thread);
        var official = runtime.CreateClient(threadId: thread);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAdUlEQVR4nO3PAQkAMAzAsIm9fwu7jDIIVEA68/Z2vcBALTBQCwzUAgO1wEAtMFALDNQCA7XAQC0wUAsM1AIDtcBALTBQCwzUAgO1wEAtMFALDNQCA7XAQC0wUAsM1AIDtcBALTBQCwzUAgO1wEAtMFALDOzpPp47MUsiR9jcAAAAAElFTkSuQmCC");
        Console.WriteLine("Disposable feature test thread: " + thread);
        if (feature == "upload")
        {
            var files = official.GetOpenAIFileClient();
            var file = (await files.UploadFileAsync(new MemoryStream(png), "sdk-pixel.png", global::OpenAI.Files.FileUploadPurpose.Vision)).Value;
            if (!(await files.DownloadFileAsync(file.Id)).Value.ToArray().SequenceEqual(png)) throw new SdkException("Uploaded file download did not match the original bytes.", "live_file_roundtrip_failed");
            Console.WriteLine("Official file upload and download round-trip succeeded.");
            var image = global::OpenAI.Chat.ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(png), "image/png");
            var text = global::OpenAI.Chat.ChatMessageContentPart.CreateTextPart("Describe this tiny image briefly. If it is too small, say so.");
            var answer = (await official.GetChatClient(args[2]).CompleteChatAsync([new global::OpenAI.Chat.UserChatMessage(text, image)])).Value;
            if (string.Concat(answer.Content.Select(c => c.Text)).Length == 0) throw new SdkException("Image input produced no text.", "live_image_input_failed");
            await VerifyRemote(runtime, scope);
            Console.WriteLine("Official binary image input was accepted, with independent remote history verification.");
        }
        else if (feature is "image" or "image-edit")
        {
            var images = official.GetImageClient(args[2]);
            var result = feature == "image" ? await images.GenerateImageAsync("A simple blue circle centered on a white background. No text.") :
                await images.GenerateImageEditAsync(new MemoryStream(png), "sdk-pixel.png", "Expand the canvas and draw a simple blue circle centered on a white background. No text.");
            var bytes = result.Value.ImageBytes.ToArray();
            if (bytes.Length < 100) throw new SdkException("Generated image was unexpectedly empty.", "live_image_generation_failed");
            await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "sdk-" + feature + ".png"), bytes);
            await VerifyRemote(runtime, scope);
            Console.WriteLine("Official " + feature + " returned downloaded image bytes. Test artifact saved beside the local session config.");
        }
        else if (feature == "edit-regenerate")
        {
            await runtime.Web.SendAsync(scope, "Reply with the word ORIGINAL only.", args[2]);
            Console.WriteLine("Created the disposable conversation.");
            await VerifyManagement(runtime, scope, args[2]);
        }
        else if (feature == "temporary")
        {
            var marker = Guid.NewGuid().ToString("N");
            var first = await runtime.Web.SendAsync(scope, new() { Model = args[2], Messages = [WebInputMessage.User("Remember the marker " + marker + ". Reply OK.")], TemporaryChat = true });
            var second = await runtime.Web.SendAsync(scope, new() { Model = args[2], Messages = [WebInputMessage.User("What marker did I give you? Reply with the marker only.")], TemporaryChat = true });
            if (first.Response.ConversationId != second.Response.ConversationId || !second.Text.Contains(marker, StringComparison.Ordinal)) throw new SdkException("Temporary continuation lost its context.", "live_temporary_continuation_failed");
            var saved = await File.ReadAllTextAsync(Path.Combine(config["sessionDirectory"]!.GetValue<string>(), scope.StorageKey + ".json"));
            if (saved.Contains(marker, StringComparison.Ordinal) || saved.Contains(second.Response.ConversationId, StringComparison.Ordinal)) throw new SdkException("Temporary content was written to disk.", "temporary_storage_failed");
            Console.WriteLine("Temporary chat continued with remembered context, without writing its content or remote ID to disk.");
        }
        else if (feature == "responses-stream")
        {
            var output = ""; string? responseId = null;
            await foreach (var update in official.GetResponsesClient().CreateResponseStreamingAsync(args[2], "Reply exactly STREAM_OK."))
            {
                if (update is global::OpenAI.Responses.StreamingResponseOutputTextDeltaUpdate delta) output += delta.Delta;
                if (update is global::OpenAI.Responses.StreamingResponseCompletedUpdate completed) responseId = completed.Response.Id;
            }
            if (!output.Contains("STREAM_OK", StringComparison.Ordinal) || responseId is null) throw new SdkException("Official Responses streaming did not complete.", "live_responses_stream_failed");
            if (!(await official.GetResponsesClient().GetResponseAsync(responseId)).Value.GetOutputText().Contains("STREAM_OK", StringComparison.Ordinal)) throw new SdkException("Recorded streaming response retrieval failed.", "live_response_retrieval_failed");
            await VerifyRemote(runtime, scope);
            Console.WriteLine("Official Responses streaming, completed-response retrieval and remote history verification succeeded.");
        }
        else if (feature == "chat-stream")
        {
            var output = ""; global::OpenAI.Chat.ChatFinishReason? finish = null;
            await foreach (var update in official.GetChatClient(args[2]).CompleteChatStreamingAsync("Reply exactly CHAT_STREAM_OK."))
            {
                output += string.Concat(update.ContentUpdate.Select(part => part.Text));
                finish = update.FinishReason ?? finish;
            }
            if (!output.Contains("CHAT_STREAM_OK", StringComparison.Ordinal) || finish != global::OpenAI.Chat.ChatFinishReason.Stop)
                throw new SdkException("Official Chat Completions streaming did not finish correctly.", "live_chat_stream_failed");
            await VerifyRemote(runtime, scope);
            Console.WriteLine("Official Chat Completions typed streaming updates, stop marker and remote history verification succeeded.");
        }
        else if (feature == "conversations")
        {
            var conversations = official.GetConversationClient();
            var conversation = (await conversations.CreateConversationAsync(new global::OpenAI.Conversations.ConversationCreationOptions { Metadata = { ["test"] = "sdk-disposable" } })).Value;
            var response = (await official.GetResponsesClient().CreateResponseAsync(new global::OpenAI.Responses.CreateResponseOptions(args[2], [global::OpenAI.Responses.ResponseItem.CreateUserMessageItem("Reply exactly CONVERSATION_OK.")])
            { ConversationOptions = new(conversation.Id) })).Value;
            if (!response.GetOutputText().Contains("CONVERSATION_OK", StringComparison.Ordinal) || response.ConversationOptions?.ConversationId != conversation.Id)
                throw new SdkException("Response was not bound to the virtual conversation resource.", "live_conversation_binding_failed");
            if ((await conversations.GetConversationAsync(conversation.Id)).Value.Metadata["test"] != "sdk-disposable")
                throw new SdkException("Conversation resource metadata was not preserved.", "live_conversation_metadata_failed");
            var ids = new List<string>();
            await foreach (var page in conversations.GetConversationItemsAsync(conversation.Id, limit: 1).GetRawPagesAsync())
            {
                var body = JsonNode.Parse(page.GetRawResponse().Content.ToString())!;
                ids.AddRange(body["data"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
            }
            if (ids.Count != 2 || JsonNode.Parse((await conversations.GetConversationItemAsync(conversation.Id, ids[0])).GetRawResponse().Content.ToString())?["id"]?.GetValue<string>() != ids[0])
                throw new SdkException("Conversation item paging or retrieval failed.", "live_conversation_items_failed");
            await conversations.UpdateConversationAsync(conversation.Id, new global::OpenAI.Conversations.ConversationUpdateOptions { Metadata = { ["test"] = "sdk-updated" } });
            if ((await conversations.GetConversationAsync(conversation.Id)).Value.Metadata["test"] != "sdk-updated")
                throw new SdkException("Conversation metadata update failed.", "live_conversation_metadata_failed");
            await VerifyRemote(runtime, runtime.ResolveScope(runtime.ClientKey, conversation.Id));
            if (!(await conversations.DeleteConversationAsync(conversation.Id)).Value.Deleted)
                throw new SdkException("Conversation resource deletion failed.", "live_conversation_delete_failed");
            Console.WriteLine("Official Conversations create/bind, metadata, paginated items, single item retrieval and remote deletion succeeded.");
        }
        else throw new ArgumentException("Unknown smoke feature: upload, image, image-edit, edit-regenerate, temporary, responses-stream, chat-stream or conversations.");
        }
        return 0;
    }
    Console.Error.WriteLine("Commands:\n  inspect-har <capture.har>\n  browser-check <config.session.json>\n  import-har <capture.har> <new-config.session.json>\n  smoke <config.session.json> [--send <web-model-slug>]\n  smoke-sdk <config.session.json> [--send <web-model-slug>]\n  resume-sdk <config.session.json> <saved-sdk-smoke-thread> <web-model-slug>\n  manage-sdk <config.session.json> <saved-sdk-edit-regenerate-thread> <web-model-slug>\n  smoke-feature <config.session.json> <web-model-slug> <upload|image|image-edit|edit-regenerate|temporary|responses-stream|chat-stream|conversations>\n  smoke-features <config.session.json> <web-model-slug> <comma-separated-features>\n  inspect-sdk <config.session.json> <saved-sdk-thread> <private-snapshot.json> [--reconcile]\nThe smoke command reads models unless --send explicitly requests a new two-turn ChatGPT conversation. Feature checks create their own test conversations.");
    return 2;
}

catch (Exception ex)
{
    Console.Error.WriteLine(ex is SdkException sdk ? $"{sdk.Code}: {sdk.Message}" : ex is System.ClientModel.ClientResultException result ? $"SDK HTTP {result.Status}: the virtual endpoint rejected the operation." : $"{ex.GetType().Name}: The operation failed. Check the input file, JSON format, file permissions and network configuration.");
    if (Environment.GetEnvironmentVariable("CHATGPT_WEB_DIAGNOSTICS") == "1")
    {
        Console.Error.WriteLine(ex.StackTrace);
        if (ex is System.ClientModel.ClientResultException resultError)
        {
            try { var error = JsonNode.Parse(resultError.GetRawResponse()?.Content.ToString() ?? "{}"); Console.Error.WriteLine("Error code: " + error?["error"]?["code"]?.GetValue<string>()); } catch { }
        }
    }
    return 1;
}

static global::OpenAI.ChatGPTWebMode Mode(JsonNode config) => Enum.TryParse<global::OpenAI.ChatGPTWebMode>(config["mode"]?.GetValue<string>() ?? "hybrid", true, out var mode) && Enum.IsDefined(mode) ? mode : throw new ArgumentException("Invalid initialization mode.");

static async Task VerifyRemote(global::OpenAI.ChatGPTWebRuntime runtime, ConversationScope scope)
{
    var state = await runtime.Web.GetStateAsync(scope);
    var remote = await runtime.Web.Transport.GetConversationAsync(scope.AccountId, state.ConversationId!);
    if (remote["mapping"] is not JsonObject mapping || !state.History.All(m => mapping.Any(n => n.Value?["message"]?["id"]?.GetValue<string>() == m.Id)))
        throw new SdkException("Independent remote graph did not confirm every local message.", "live_remote_verification_failed");
    Console.WriteLine("Verified remote conversation: https://chatgpt.com/c/" + state.ConversationId);
}

static async Task VerifyManagement(global::OpenAI.ChatGPTWebRuntime runtime, ConversationScope scope, string model)
{
    var state = await runtime.Web.GetStateAsync(scope);
    if (state.ConversationId is null || state.RequiresReconciliation || state.History.FirstOrDefault()?.Text is not ("Reply with the word ORIGINAL only." or "Reply with the word EDITED only."))
        throw new ArgumentException("This thread is not a confirmed disposable SDK management conversation.");
    if (state.History[0].Text == "Reply with the word ORIGINAL only.")
    {
        var edited = await runtime.Web.EditAsync(scope, state.History[0].Id, "Reply with the word EDITED only.", model);
        if (!edited.Text.Contains("EDITED", StringComparison.Ordinal)) throw new SdkException("Editing did not produce the requested branch.", "live_edit_failed");
        Console.WriteLine("Editing produced the new remote branch.");
    }
    state = await runtime.Web.GetStateAsync(scope);
    var previousAssistant = state.ParentMessageId;
    var regenerated = await runtime.Web.RegenerateAsync(scope, model);
    if (regenerated.Response.MessageId == previousAssistant) throw new SdkException("Regeneration did not return a new assistant node.", "live_regeneration_failed");
    await VerifyRemote(runtime, scope);
    await runtime.Web.UpdateConversationAsync(scope, title: "SDK disposable management test", archived: true);
    await runtime.Web.UpdateConversationAsync(scope, archived: false);
    await runtime.Web.UpdateConversationAsync(scope, delete: true);
    if ((await runtime.Web.GetStateAsync(scope)).ConversationId is not null) throw new SdkException("Deleted conversation binding was not cleared.", "live_delete_binding_failed");
    Console.WriteLine("Remote edit, regeneration, rename, archive/unarchive and deletion succeeded; the local binding is cleared.");
}

static ChatGPTWebSdk.Browser.BrowserSentinelOptions Browser(JsonNode config)
{
    var configured = config["browser"]?.Deserialize<ChatGPTWebSdk.Browser.BrowserSentinelOptions>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
    return new() { CdpEndpoint = configured.CdpEndpoint, Channel = configured.Channel, ExecutablePath = configured.ExecutablePath, UseDesktopLauncher = configured.UseDesktopLauncher, ComposerSelector = configured.ComposerSelector, SendButtonSelector = configured.SendButtonSelector,
        Progress = Console.WriteLine, Timeout = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("CHATGPT_WEB_BROWSER_TIMEOUT"), out var seconds) ? seconds : configured.Timeout.TotalSeconds),
        MaxRetries = configured.MaxRetries, RetryDelay = configured.RetryDelay,
        DiagnosticScreenshotPath = Environment.GetEnvironmentVariable("CHATGPT_WEB_BROWSER_SCREENSHOT"), DiagnosticComposerPath = Environment.GetEnvironmentVariable("CHATGPT_WEB_BROWSER_COMPOSER"), DiagnosticHandshakePath = Environment.GetEnvironmentVariable("CHATGPT_WEB_BROWSER_HANDSHAKE") };
}

static TimeSpan BrowserRequestTimeout(JsonNode config)
{
    var browser = Browser(config);
    return browser.Timeout * (browser.MaxRetries + 1) + browser.RetryDelay * browser.MaxRetries + TimeSpan.FromMinutes(10);
}

static HttpClient DiagnosticHttp(JsonNode config)
{
    var driver = config["httpDriver"]?.GetValue<string>() ?? "systemCurl";
    if (driver == "dotNet") return WebHttpClient.Create();
    if (driver != "systemCurl") throw new ArgumentException("Invalid HTTP driver.");
    var path = Environment.GetEnvironmentVariable("CHATGPT_WEB_HTTP_ERROR_FILE");
    var streamPath = Environment.GetEnvironmentVariable("CHATGPT_WEB_HTTP_STREAM_FILE");
    return path is null && streamPath is null ? WebHttpClient.CreateCurl() : new HttpClient(new HttpDiagnosticCapture(path, streamPath) { InnerHandler = new CurlHttpMessageHandler() }) { Timeout = TimeSpan.FromMinutes(10) };
}

sealed class HttpDiagnosticCapture(string? errorPath, string? streamPath) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && errorPath is not null)
        {
            Console.Error.WriteLine("Rejected upstream operation: " + request.Method + " " + request.RequestUri!.AbsolutePath + " (HTTP " + (int)response.StatusCode + ").");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length <= 1024 * 1024) await File.WriteAllBytesAsync(errorPath, bytes, ct);
        }
        if (streamPath is not null && response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath.EndsWith("/conversation", StringComparison.Ordinal))
        {
            // Explicit private diagnostics only; normal requests remain unbuffered and streaming.
            await response.Content.LoadIntoBufferAsync(64 * 1024 * 1024, ct);
            await File.WriteAllBytesAsync(streamPath, await response.Content.ReadAsByteArrayAsync(ct), ct);
        }
        return response;
    }
}
