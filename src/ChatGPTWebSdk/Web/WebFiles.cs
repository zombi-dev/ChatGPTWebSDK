using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

public sealed class WebFileUploadOptions
{
    public required string FileName { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool StoreInLibrary { get; init; }
    public bool IndexForRetrieval { get; init; }
    public string Purpose { get; init; } = "user_data";
    public long MaxBytes { get; init; } = 64 * 1024 * 1024;
}

public sealed partial class ChatGptWebTransport
{
    public async Task<WebUploadedFile> UploadFileAsync(string account, Stream content, WebFileUploadOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.FileName) || options.FileName.Any(char.IsControl) || options.MaxBytes <= 0) throw new ArgumentException("Invalid upload options.");
        // A bounded buffer supports non-seekable input and supplies the exact file_size required by the web endpoint.
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(bytes, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > options.MaxBytes) throw new ArgumentException("Upload exceeds MaxBytes.");
            await buffer.WriteAsync(bytes.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        var creation = await SendJsonAsync(account, HttpMethod.Post, _options.Endpoints.Files, new JsonObject
        {
            ["file_name"] = options.FileName, ["file_size"] = buffer.Length, ["mime_type"] = options.MimeType,
            ["client_resolved_mime_type"] = options.MimeType, ["mime_resolution_source"] = "user_provided",
            ["use_case"] = "multimodal", ["store_in_library"] = options.StoreInLibrary,
            ["reset_rate_limits"] = false, ["supports_direct_azure_multipart"] = true,
            ["timezone_offset_min"] = _options.Endpoints.ConversationDefaults["timezone_offset_min"]?.DeepClone() ?? JsonValue.Create(0)
        }, ct).ConfigureAwait(false);
        var id = creation["file_id"]?.GetValue<string>() ?? throw new SdkException("Upload response has no file ID.", "unsupported_file_response");
        var upload = AssetUri(creation["upload_url"]?.GetValue<string>() ?? throw new SdkException("Upload response has no URL.", "unsupported_file_response"));
        if (upload.Authority == _options.BaseUri.Authority) throw new SdkException("Expected an external signed upload URL.", "unsupported_file_response");
        using (var request = new HttpRequestMessage(HttpMethod.Put, upload))
        {
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Headers.Add("x-ms-version", "2020-04-08");
            request.Headers.Add("x-ms-blob-content-type", options.MimeType);
            request.Content = new ByteArrayContent(buffer.ToArray());
            request.Content.Headers.ContentType = new(options.MimeType);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
        var credentials = await GetCredentialsAsync(account, ct).ConfigureAwait(false);
        var processBody = new JsonObject { ["file_id"] = id, ["file_name"] = options.FileName, ["mime_type"] = options.MimeType,
            ["use_case"] = "multimodal", ["index_for_retrieval"] = options.IndexForRetrieval,
            ["metadata"] = new JsonObject { ["store_in_library"] = options.StoreInLibrary } };
        using (var request = Build(credentials, HttpMethod.Post, _options.Endpoints.ProcessUpload, processBody))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(input);
            bool completed = false;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) line = line[5..].TrimStart();
                if (!line.StartsWith('{')) continue;
                var progress = JsonNode.Parse(line);
                if (progress?["error"] is not null || progress?["event"]?.GetValue<string>() is "file.processing.failed" or "file.processing.error")
                    throw new SdkException("File processing failed.", "web_file_processing_failed");
                if (progress?["event"]?.GetValue<string>() == "file.processing.completed") completed = true;
            }
            if (!completed) throw new SdkException("Upload stream did not confirm processing completion. No automatic upload retry was attempted.", "web_file_processing_incomplete");
        }
        return new(id, options.FileName, options.MimeType, buffer.Length, options.Width, options.Height) { Purpose = options.Purpose, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
    }

    public Task<JsonNode> GetFileAsync(string account, string fileId, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.FileById.Replace("{file_id}", Segment(fileId), StringComparison.Ordinal), ct: ct);

    public Task<JsonNode> GetFileDownloadInfoAsync(string account, string fileId, CancellationToken ct = default) =>
        SendJsonAsync(account, HttpMethod.Get, _options.Endpoints.FileDownload.Replace("{file_id}", Segment(fileId), StringComparison.Ordinal), ct: ct);

    public async Task DownloadFileAsync(string account, string fileId, Stream destination, CancellationToken ct = default)
    {
        var info = await GetFileDownloadInfoAsync(account, fileId, ct).ConfigureAwait(false);
        var uri = AssetUri(info["download_url"]?.GetValue<string>() ?? throw new SdkException("File download response has no URL.", "unsupported_file_response"));
        using var request = uri.Authority == _options.BaseUri.Authority
            ? Build(await GetCredentialsAsync(account, ct).ConfigureAwait(false), HttpMethod.Get, uri.PathAndQuery.TrimStart('/'), null)
            : new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private Uri AssetUri(string url)
    {
        if (!Uri.TryCreate(_options.BaseUri, url, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            (uri.Authority != _options.BaseUri.Authority && uri.Host != "files.openai.com" && !uri.Host.EndsWith(".oaiusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new SdkException("Asset endpoint returned an unexpected origin. No account credentials were forwarded.", "untrusted_asset_origin");
        return uri;
    }
}
