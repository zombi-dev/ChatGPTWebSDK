// Image normalization adapted from EvanZhouDev/openai-oauth (Apache-2.0).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

internal static class CodexImages
{
    private const int MaxUploadBytes = 64 * 1024 * 1024;
    internal static async Task<JsonObject> PrepareAsync(HttpContent content, bool edit, CancellationToken ct)
    {
        var data = new JsonObject(); var images = new JsonArray();
        if (edit)
        {
            var parts = await MultipartAsync(content, ct).ConfigureAwait(false);
            foreach (var part in parts)
            {
                if (part.Name is "image" or "image[]")
                {
                    if (part.Bytes.Length > 50 * 1024 * 1024) throw new ArgumentException("A reference image exceeds the 50 MiB limit.");
                    var mime = part.Mime == "application/octet-stream" ? Path.GetExtension(part.FileName)?.ToLowerInvariant() switch
                        { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/png" } : part.Mime;
                    if (mime is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) throw new ArgumentException("Unsupported reference image type.");
                    images.Add(new JsonObject { ["image_url"] = "data:" + mime + ";base64," + Convert.ToBase64String(part.Bytes) });
                }
                else if (!data.TryAdd(part.Name, Encoding.UTF8.GetString(part.Bytes))) throw new ArgumentException("Repeated image option.");
            }
            if (images.Count is < 1 or > 5) throw new ArgumentException("Codex image editing requires one to five reference images.");
        }
        else data = JsonNode.Parse(Encoding.UTF8.GetString(await ReadBodyAsync(content, ct).ConfigureAwait(false))) as JsonObject ?? throw new ArgumentException("Image generation requires JSON.");
        foreach (var name in new[] { "mask", "input_fidelity", "moderation", "output_compression", "output_format", "partial_images" })
            if (data[name] is not null) throw new SdkException("This image option is unavailable on the Codex OAuth endpoint: " + name + ".", "codex_operation_unsupported");
        if (data["stream"]?.ToString() is "true" or "True" || data["response_format"] is not null && data["response_format"]!.GetValue<string>() != "b64_json")
            throw new SdkException("Codex images return non-streaming base64 JSON.", "codex_operation_unsupported");
        if (data["prompt"]?.GetValue<string>() is not { Length: > 0 } prompt) throw new ArgumentException("Image prompt is required.");
        var normalized = new JsonObject { ["model"] = data["model"]?.GetValue<string>() ?? "gpt-image-2", ["prompt"] = prompt };
        foreach (var key in new[] { "background", "quality", "size" }) if (data[key] is { } value) normalized[key] = value.DeepClone();
        if (data["n"] is { } count)
        {
            if (!int.TryParse(count.ToString(), out var number) || number is < 1 or > 10) throw new ArgumentException("Invalid image count.");
            normalized["n"] = number;
        }
        if (edit) normalized["images"] = images;
        return normalized;
    }
    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxUploadBytes) throw new ArgumentException("Image request exceeds the 64 MiB upload limit.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false); using var data = new MemoryStream(); var buffer = new byte[81920];
        int read; while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { if (data.Length + read > MaxUploadBytes) throw new ArgumentException("Image request exceeds the 64 MiB upload limit."); await data.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
        return data.ToArray();
    }
    private sealed record Part(string Name, string? FileName, string Mime, byte[] Bytes);
    private static async Task<List<Part>> MultipartAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentType?.MediaType != "multipart/form-data") throw new ArgumentException("Image editing requires multipart/form-data.");
        var boundary = content.Headers.ContentType.Parameters.FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (boundary is not { Length: > 0 and <= 200 } || boundary.Any(char.IsControl)) throw new ArgumentException("Invalid multipart boundary.");
        var bytes = await ReadBodyAsync(content, ct).ConfigureAwait(false); var startBoundary = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
        if (!bytes.AsSpan().StartsWith(startBoundary)) throw new ArgumentException("Invalid multipart content.");
        var separator = Encoding.ASCII.GetBytes("\r\n--" + boundary); var position = startBoundary.Length; var result = new List<Part>();
        while (position < bytes.Length)
        {
            var headerEnd = bytes.AsSpan(position).IndexOf("\r\n\r\n"u8); if (headerEnd is < 0 or > 16384) throw new ArgumentException("Invalid multipart headers.");
            var headers = Encoding.UTF8.GetString(bytes, position, headerEnd).Split("\r\n"); var start = position + headerEnd + 4;
            var end = bytes.AsSpan(start).IndexOf(separator); if (end < 0) throw new ArgumentException("Incomplete multipart content.");
            var disposition = headers.FirstOrDefault(h => h.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            if (disposition is null || !ContentDispositionHeaderValue.TryParse(disposition[20..].Trim(), out var parsed) || parsed.Name is null)
                throw new ArgumentException("Invalid multipart disposition.");
            result.Add(new(parsed.Name.Trim('"'), (parsed.FileNameStar ?? parsed.FileName)?.Trim('"'), headers.FirstOrDefault(h => h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?[13..].Trim() ?? "application/octet-stream", bytes.AsSpan(start, end).ToArray()));
            if (result.Count > 100) throw new ArgumentException("Too many multipart parts.");
            position = start + end + separator.Length;
            if (bytes.AsSpan(position).StartsWith("--"u8)) return result;
            if (!bytes.AsSpan(position).StartsWith("\r\n"u8)) throw new ArgumentException("Incomplete multipart separator.");
            position += 2;
        }
        throw new ArgumentException("Incomplete multipart content.");
    }
}
