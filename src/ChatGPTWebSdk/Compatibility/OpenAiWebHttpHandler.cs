using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Compatibility;

/// <summary>In-process OpenAI HTTP compatibility layer. It never forwards a request to the platform API.</summary>
public sealed class OpenAiWebHttpHandler(OpenAiWebAdapter adapter, Func<string, string?, ConversationScope> resolveScope) : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct).ConfigureAwait(false).GetAwaiter().GetResult();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await DispatchAsync(request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var sdk = ex as SdkException;
            bool invalid = ex is ArgumentException or JsonException or InvalidOperationException;
            return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = sdk?.Message ?? (invalid ? "Invalid request body or parameters." : "The upstream request failed. No automatic resend was attempted."),
                ["type"] = sdk?.Code ?? "request_error", ["code"] = sdk?.Code ?? "request_error", ["param"] = null } }, sdk?.StatusCode ?? (invalid ? HttpStatusCode.BadRequest : HttpStatusCode.BadGateway));
        }
    }
    private async Task<HttpResponseMessage> DispatchAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var authorization = request.Headers.Authorization;
        if (authorization?.Scheme != "Bearer" || string.IsNullOrEmpty(authorization.Parameter)) throw new SdkException("An application client key is required.", "authentication_required", HttpStatusCode.Unauthorized);
        var thread = request.Headers.TryGetValues("X-ChatGPT-Thread-Id", out var values) ? values.Single() : null;
        var scope = resolveScope(authorization.Parameter, thread);
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.StartsWith("/v1/", StringComparison.Ordinal)) path = path[3..];
        var segments = path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        var verb = request.Method.Method;
        if (segments[0] == "models" && verb == "GET")
        {
            var models = await adapter.ListModelsAsync(scope, ct).ConfigureAwait(false);
            if (segments.Length == 1) return Json(models);
            return Json(models["data"]!.AsArray().FirstOrDefault(m => m!["id"]?.GetValue<string>() == segments[1])?.DeepClone()
                ?? throw new SdkException("Model not found.", "model_not_found", HttpStatusCode.NotFound));
        }
        if (segments[0] == "responses" && segments.Length >= 2)
        {
            if (thread is null) scope = await adapter.Client.ResolveResponseScopeAsync(scope, segments[1], ct).ConfigureAwait(false);
            var response = await adapter.Client.GetResponseAsync(scope, segments[1], ct).ConfigureAwait(false);
            if (verb == "GET" && segments.Length == 2)
            {
                if (request.RequestUri!.Query.Length > 0) throw new UnsupportedWebFeatureException("response retrieval query options");
                var json = OpenAiWebAdapter.ResponseJson(response);
                if (scope.ThreadId.StartsWith("conv_web_", StringComparison.Ordinal)) json["conversation"] = new JsonObject { ["id"] = scope.ThreadId };
                return Json(json);
            }
            if (verb == "DELETE" && segments.Length == 2)
            {
                await adapter.Client.DeleteResponseAsync(scope, response.Id, ct).ConfigureAwait(false);
                return Json(new JsonObject { ["id"] = response.Id, ["object"] = "response", ["deleted"] = true });
            }
            if (verb == "GET" && segments.Length == 3 && segments[2] == "input_items") return Json(MessageList(response.Input, request.RequestUri!.Query));
        }
        if (segments[0] == "conversations") return await ConversationAsync(scope, segments, verb, request, ct).ConfigureAwait(false);
        if (segments[0] == "files") return await FilesAsync(scope, segments, verb, request, ct).ConfigureAwait(false);
        if (path is "/images/generations" or "/images/edits" && verb == "POST") return await ImageAsync(scope, path, request, ct).ConfigureAwait(false);
        if (verb == "POST" && path is "/chat/completions" or "/responses")
        {
            var generation = GenerationAdapter(request);
            var body = await Body(request, ct).ConfigureAwait(false);
            if (body["conversation"] is not null && body["previous_response_id"] is not null) throw new ArgumentException("conversation and previous_response_id cannot be combined.");
            if (thread is null && path == "/responses" && body["previous_response_id"]?.GetValue<string>() is { } previous)
                scope = await adapter.Client.ResolveResponseScopeAsync(scope, previous, ct).ConfigureAwait(false);
            if (body["conversation"] is { } conversation)
            {
                var id = conversation is JsonObject obj ? obj["id"]?.GetValue<string>() : conversation.GetValue<string>();
                if (id is null) throw new ArgumentException("Missing conversation ID.");
                if (id.StartsWith("conv_web_", StringComparison.Ordinal))
                {
                    scope = new(scope.AccountId, scope.UserId, id);
                    if ((await adapter.Client.GetStateAsync(scope, ct).ConfigureAwait(false)).ConversationAlias != id) throw new SdkException("Conversation not found.", "conversation_not_found", HttpStatusCode.NotFound);
                    body.Remove("conversation");
                }
                else if ((await adapter.Client.GetStateAsync(scope, ct).ConfigureAwait(false)).ConversationId != id)
                    throw new SdkException("Link an external conversation explicitly through the native web client before using it.", "external_conversation_link_denied", HttpStatusCode.Forbidden);
            }
            if (body["stream"]?.GetValue<bool>() == true)
            {
                var events = path == "/responses" ? generation.ResponsesAsync(scope, body, ct) : generation.ChatAsync(scope, body, ct);
                var iterator = events.GetAsyncEnumerator(ct);
                try
                {
                    if (!await iterator.MoveNextAsync().ConfigureAwait(false)) throw new SdkException("Generation returned no stream events.", "incomplete_web_response");
                    var content = new StreamContent(new EventReadStream(iterator, iterator.Current));
                    content.Headers.ContentType = new("text/event-stream");
                    return new(HttpStatusCode.OK) { Content = content, RequestMessage = request };
                }
                catch { await iterator.DisposeAsync().ConfigureAwait(false); throw; }
            }
            var turn = path == "/responses" ? await generation.PrepareResponseAsync(scope, body, ct).ConfigureAwait(false) : await generation.PrepareChatAsync(scope, body, ct).ConfigureAwait(false);
            var result = await adapter.Client.SendAsync(scope, turn, ct).ConfigureAwait(false);
            var output = path == "/responses" ? OpenAiWebAdapter.ResponseJson(result.Response) : OpenAiWebAdapter.ChatJson(result.Response);
            if (scope.ThreadId.StartsWith("conv_web_", StringComparison.Ordinal) && path == "/responses") output["conversation"] = new JsonObject { ["id"] = scope.ThreadId };
            return Json(output);
        }
        throw new UnsupportedWebFeatureException(verb + " " + path);
    }

    private OpenAiWebAdapter GenerationAdapter(HttpRequestMessage request)
    {
        var project = request.Headers.TryGetValues("X-ChatGPT-Project-Id", out var projectValues) ? projectValues.Single() : null;
        bool? temporary = request.Headers.TryGetValues("X-ChatGPT-Temporary-Chat", out var temporaryValues)
            ? bool.TryParse(temporaryValues.Single(), out var parsedTemporary) ? parsedTemporary : throw new ArgumentException("X-ChatGPT-Temporary-Chat must be true or false.") : null;
        return adapter.WithContext(project, temporary);
    }

    private async Task<HttpResponseMessage> ConversationAsync(ConversationScope scope, string[] path, string verb, HttpRequestMessage request, CancellationToken ct)
    {
        if (path.Length == 1 && verb == "POST")
        {
            var body = await Body(request, ct).ConfigureAwait(false);
            Fields(body, "items", "metadata");
            if (body["items"] is JsonArray { Count: > 0 }) throw new UnsupportedWebFeatureException("conversation creation with initial items");
            var id = "conv_web_" + Guid.NewGuid().ToString("N");
            await using var lease = await adapter.Client.Store.AcquireAsync(new(scope.AccountId, scope.UserId, id), ct).ConfigureAwait(false);
            lease.State.ConversationAlias = id;
            if (body["metadata"] is JsonObject metadata) lease.State.Metadata = (JsonObject)metadata.DeepClone();
            await lease.SaveAsync(ct).ConfigureAwait(false);
            return Json(ConversationJson(lease.State));
        }
        if (path.Length < 2 || !path[1].StartsWith("conv_web_", StringComparison.Ordinal)) throw new UnsupportedWebFeatureException("unbound conversation ID");
        scope = new(scope.AccountId, scope.UserId, path[1]);
        await using var stateLease = await adapter.Client.Store.AcquireAsync(scope, ct).ConfigureAwait(false);
        var state = stateLease.State;
        if (state.ConversationAlias != path[1]) throw new SdkException("Conversation not found.", "conversation_not_found", HttpStatusCode.NotFound);
        if (verb == "GET" && path.Length == 2) return Json(ConversationJson(state));
        if (verb == "POST" && path.Length == 2)
        {
            var body = await Body(request, ct).ConfigureAwait(false); Fields(body, "metadata");
            if (body["metadata"] is JsonObject metadata) state.Metadata = (JsonObject)metadata.DeepClone();
            await stateLease.SaveAsync(ct).ConfigureAwait(false); return Json(ConversationJson(state));
        }
        if (verb == "DELETE" && path.Length == 2)
        {
            if (state.RequiresReconciliation) throw new ConversationReconciliationException();
            if (state.ConversationId is { } remote) await adapter.Client.Transport.DeleteConversationAsync(scope.AccountId, remote, ct).ConfigureAwait(false);
            state.ConversationId = null; state.ConversationAlias = null; state.History.Clear(); state.Responses.Clear(); state.LastResponseId = null;
            await stateLease.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            return Json(new JsonObject { ["id"] = path[1], ["object"] = "conversation.deleted", ["deleted"] = true });
        }
        if (verb == "GET" && path.Length == 3 && path[2] == "items") return Json(MessageList(state.History, request.RequestUri!.Query));
        if (verb == "GET" && path.Length == 4 && path[2] == "items") return Json(Message(state.History.FirstOrDefault(m => m.Id == path[3]) ?? throw new SdkException("Item not found.", "item_not_found", HttpStatusCode.NotFound)));
        throw new UnsupportedWebFeatureException("requested conversation item operation");
    }

    private async Task<HttpResponseMessage> FilesAsync(ConversationScope scope, string[] path, string verb, HttpRequestMessage request, CancellationToken ct)
    {
        if (verb == "POST" && path.Length == 1)
        {
            var parts = await MultipartAsync(request, ct).ConfigureAwait(false);
            var purpose = parts.FirstOrDefault(p => p.Name == "purpose")?.Text ?? "user_data";
            if (purpose is not ("user_data" or "vision")) throw new UnsupportedWebFeatureException("file purpose " + purpose);
            if (parts.Any(p => p.Name is not ("file" or "purpose"))) throw new UnsupportedWebFeatureException("upload options");
            var part = parts.SingleOrDefault(p => p.Name == "file") ?? throw new ArgumentException("Missing file.");
            var mime = FileMime(part);
            var dimensions = mime.StartsWith("image/", StringComparison.Ordinal) ? ImageDimensions.Read(part.Bytes, mime) : (0, 0);
            using var input = new MemoryStream(part.Bytes, false);
            var uploaded = await adapter.UploadOwnedFileAsync(scope, input, new() { FileName = part.FileName ?? "upload.bin", MimeType = mime, Width = dimensions.Item1, Height = dimensions.Item2, Purpose = purpose, IndexForRetrieval = !mime.StartsWith("image/", StringComparison.Ordinal) }, ct).ConfigureAwait(false);
            return Json(FileJson(uploaded));
        }
        if (verb == "GET" && path.Length == 1)
        {
            string? purpose = null;
            foreach (var field in request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = field.Split('=', 2);
                if (pair[0] != "purpose") throw new UnsupportedWebFeatureException("file list query " + pair[0]);
                purpose = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
            }
            await using var lease = await adapter.Client.Store.AcquireAsync(new(scope.AccountId, scope.UserId, "__sdk_files"), ct).ConfigureAwait(false);
            return Json(new JsonObject { ["object"] = "list", ["data"] = new JsonArray(lease.State.Files.Values.Where(f => purpose is null || f.Purpose == purpose).Select(f => (JsonNode)FileJson(f)).ToArray()), ["has_more"] = false });
        }
        if (verb == "GET" && path.Length >= 2)
        {
            var file = await adapter.GetOwnedFileAsync(scope, path[1], ct).ConfigureAwait(false);
            if (path.Length == 2) return Json(FileJson(file));
            if (path.Length == 3 && path[2] == "content")
            {
                var output = new MemoryStream();
                try { await adapter.Client.Transport.DownloadFileAsync(scope.AccountId, file.Id, output, ct).ConfigureAwait(false); output.Position = 0; }
                catch { output.Dispose(); throw; }
                var content = new StreamContent(output); content.Headers.ContentType = new(file.MimeType);
                return new(HttpStatusCode.OK) { Content = content };
            }
        }
        throw new UnsupportedWebFeatureException("requested file operation");
    }

    private async Task<HttpResponseMessage> ImageAsync(ConversationScope scope, string path, HttpRequestMessage request, CancellationToken ct)
    {
        JsonObject body;
        MultipartPart? referencePart = null;
        if (path.EndsWith("edits", StringComparison.Ordinal))
        {
            var parts = await MultipartAsync(request, ct).ConfigureAwait(false);
            if (parts.Any(p => p.Name is not ("image" or "image[]" or "model" or "prompt" or "n" or "response_format"))) throw new UnsupportedWebFeatureException("image edit options or masks");
            var images = parts.Where(p => p.Name is "image" or "image[]").ToArray();
            if (images.Length != 1) throw new UnsupportedWebFeatureException("multiple image edit inputs");
            referencePart = images[0];
            body = new();
            foreach (var field in parts.Where(p => p.Name is not ("image" or "image[]"))) body[field.Name] = field.Name == "n" ? JsonValue.Create(int.Parse(field.Text)) : JsonValue.Create(field.Text);
        }
        else body = await Body(request, ct).ConfigureAwait(false);
        Fields(body, "model", "prompt", "n", "response_format");
        if (body["n"]?.GetValue<int>() is { } n && n != 1) throw new UnsupportedWebFeatureException("multiple generated images");
        if (body["response_format"]?.GetValue<string>() is not (null or "b64_json")) throw new UnsupportedWebFeatureException("image URL response format");
        var prompt = body["prompt"]?.GetValue<string>() ?? throw new ArgumentException("Missing image prompt.");
        var probe = await GenerationAdapter(request).PrepareResponseAsync(scope, new JsonObject { ["model"] = body["model"]?.DeepClone(), ["input"] = "Generate an image: " + prompt, ["tools"] = new JsonArray() }, ct).ConfigureAwait(false);
        WebUploadedFile? reference = null;
        if (referencePart is { } part)
        {
            var mime = FileMime(part); var dimensions = ImageDimensions.Read(part.Bytes, mime);
            using var input = new MemoryStream(part.Bytes, false);
            reference = await adapter.UploadOwnedFileAsync(scope, input, new() { FileName = part.FileName ?? "image.png", MimeType = mime, Width = dimensions.Width, Height = dimensions.Height }, ct).ConfigureAwait(false);
        }
        var turn = reference is null ? probe : new WebTurnRequest { Model = probe.Model, Messages = [reference.ToMessage("Edit this image: " + prompt)], ProjectId = probe.ProjectId, GizmoId = probe.GizmoId, TemporaryChat = probe.TemporaryChat, UseMcp = false };
        var result = await adapter.Client.SendAsync(scope, turn, ct).ConfigureAwait(false);
        if (result.Response.Assets.Count == 0) throw new SdkException("ChatGPT returned no generated image for this turn.", "image_generation_unavailable", HttpStatusCode.UnprocessableEntity);
        var data = new JsonArray();
        foreach (var asset in result.Response.Assets)
        {
            using var image = new MemoryStream();
            await adapter.Client.Transport.DownloadFileAsync(scope.AccountId, asset.FileId, image, ct).ConfigureAwait(false);
            data.Add(new JsonObject { ["b64_json"] = Convert.ToBase64String(image.ToArray()) });
        }
        return Json(new JsonObject { ["created"] = result.Response.CreatedAt, ["data"] = data });
    }

    private static JsonObject ConversationJson(ConversationState state) => new() { ["id"] = state.ConversationAlias, ["object"] = "conversation", ["created_at"] = state.CreatedAt, ["metadata"] = state.Metadata.DeepClone(), ["chatgpt_web"] = new JsonObject { ["conversation_id"] = state.ConversationId } };
    private static JsonObject FileJson(WebUploadedFile f) => new() { ["id"] = f.Id, ["object"] = "file", ["bytes"] = f.SizeBytes, ["created_at"] = f.CreatedAt, ["filename"] = f.Name, ["purpose"] = f.Purpose, ["status"] = "processed" };
    private static JsonObject Message(StoredMessage m) => new() { ["id"] = m.Id, ["type"] = "message", ["role"] = m.Role, ["status"] = "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = m.Role == "assistant" ? "output_text" : "input_text", ["text"] = m.Text, ["annotations"] = m.Role == "assistant" ? new JsonArray() : null }) };
    private static JsonObject MessageList(IReadOnlyList<StoredMessage> messages, string query)
    {
        int limit = 20; string? after = null; string order = "desc";
        foreach (var field in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2); var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
            switch (pair[0]) { case "limit": limit = int.Parse(value); break; case "after": after = value; break; case "order": order = value; break; default: throw new UnsupportedWebFeatureException("list query " + pair[0]); }
        }
        if (limit is < 1 or > 100 || order is not ("asc" or "desc")) throw new ArgumentException("Invalid list pagination.");
        var ordered = (order == "asc" ? messages : messages.Reverse()).ToList();
        if (after is not null) { var position = ordered.FindIndex(m => m.Id == after); if (position < 0) throw new ArgumentException("Unknown pagination cursor."); ordered = ordered.Skip(position + 1).ToList(); }
        var page = ordered.Take(limit).ToArray();
        return new() { ["object"] = "list", ["data"] = new JsonArray(page.Select(m => (JsonNode)Message(m)).ToArray()), ["has_more"] = ordered.Count > limit, ["first_id"] = page.FirstOrDefault()?.Id, ["last_id"] = page.LastOrDefault()?.Id };
    }
    private static async Task<JsonObject> Body(HttpRequestMessage request, CancellationToken ct) => JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? throw new ArgumentException("Expected JSON object.");
    private static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    private static void Fields(JsonObject body, params string[] names) { foreach (var pair in body) if (pair.Value is not null && !names.Contains(pair.Key)) throw new UnsupportedWebFeatureException(pair.Key); }

    private sealed record MultipartPart(string Name, string? FileName, string ContentType, byte[] Bytes) { public string Text => Encoding.UTF8.GetString(Bytes); }
    private static string FileMime(MultipartPart part) => part.ContentType != "application/octet-stream" ? part.ContentType : Path.GetExtension(part.FileName)?.ToLowerInvariant() switch
    { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".pdf" => "application/pdf", ".txt" => "text/plain", _ => part.ContentType };
    private static async Task<List<MultipartPart>> MultipartAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var content = request.Content ?? throw new ArgumentException("Missing upload content.");
        if (content.Headers.ContentLength > 64 * 1024 * 1024) throw new ArgumentException("Upload is too large.");
        var boundary = content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"') ?? throw new ArgumentException("Missing multipart boundary.");
        if (boundary.Length is < 1 or > 200) throw new ArgumentException("Invalid multipart boundary.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var data = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) { if (data.Length + read > 64 * 1024 * 1024) throw new ArgumentException("Upload is too large."); await data.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
        var bytes = data.ToArray(); var separator = Encoding.ASCII.GetBytes("\r\n--" + boundary); var headerEnd = Encoding.ASCII.GetBytes("\r\n\r\n");
        int position = boundary.Length + 4; var parts = new List<MultipartPart>();
        while (position < bytes.Length)
        {
            int relativeEnd = bytes.AsSpan(position).IndexOf(headerEnd); if (relativeEnd < 0) break;
            int start = position + relativeEnd + 4; int relativeBoundary = bytes.AsSpan(start).IndexOf(separator); if (relativeBoundary < 0) throw new ArgumentException("Incomplete multipart content.");
            var headers = Encoding.UTF8.GetString(bytes, position, relativeEnd).Split("\r\n");
            var disposition = ContentDispositionHeaderValue.Parse(headers.First(h => h.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))[20..].Trim());
            var mime = headers.FirstOrDefault(h => h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?[13..].Trim() ?? "application/octet-stream";
            parts.Add(new(disposition.Name?.Trim('"') ?? throw new ArgumentException("Missing part name."), (disposition.FileNameStar ?? disposition.FileName)?.Trim('"'), mime, bytes.AsSpan(start, relativeBoundary).ToArray()));
            position = start + relativeBoundary + separator.Length;
            if (bytes.AsSpan(position).StartsWith("--"u8)) break;
            position += 2;
        }
        return parts;
    }

    private sealed class EventReadStream(IAsyncEnumerator<ServerSentEvent> events, ServerSentEvent first) : Stream
    {
        private byte[] _buffer = Encode(first); private int _offset; private bool _complete; private bool _disposed;
        private static byte[] Encode(ServerSentEvent item) => Encoding.UTF8.GetBytes((item.Event is null ? "" : "event: " + item.Event + "\n") + string.Concat(item.Data.Split('\n').Select(l => "data: " + l + "\n")) + "\n");
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); if (destination.Length == 0) return 0;
            if (_offset == _buffer.Length)
            { if (_complete || !await events.MoveNextAsync().ConfigureAwait(false)) { _complete = true; return 0; } _buffer = Encode(events.Current); _offset = 0; }
            int count = Math.Min(destination.Length, _buffer.Length - _offset); _buffer.AsMemory(_offset, count).CopyTo(destination); _offset += count; return count;
        }
        protected override void Dispose(bool disposing) { if (disposing && !_disposed) { _disposed = true; events.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult(); } _complete = true; base.Dispose(disposing); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
