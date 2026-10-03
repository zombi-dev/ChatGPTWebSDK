using System.Net;
using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Protocol;

internal static class HttpProtocol
{
    public static Uri RelativeUri(Uri baseUri, string path)
    {
        if (!baseUri.IsAbsoluteUri || !baseUri.AbsoluteUri.EndsWith('/'))
            throw new ArgumentException("BaseUri must be an absolute URI ending with '/'.");
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains('#') ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http")
            throw new ArgumentException("Only relative endpoint paths on the configured origin are allowed.", nameof(path));
        var uri = new Uri(baseUri, path.TrimStart('/'));
        if (uri.Scheme != baseUri.Scheme || uri.Authority != baseUri.Authority || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new ArgumentException("The endpoint escapes BaseUri.", nameof(path));
        return uri;
    }

    public static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        if (response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge"))
            throw new WebChallengeException("cloudflare");
        // Never include response bodies, prompts, cookies or bearer tokens in exceptions/logs.
        string code = $"http_{(int)response.StatusCode}";
        if (response.StatusCode == HttpStatusCode.Unauthorized) code = "authentication_required";
        if (response.StatusCode == HttpStatusCode.Forbidden) code = "web_access_denied";
        if ((int)response.StatusCode == 429) code = "rate_limit_exceeded";
        throw new SdkException($"Endpoint returned HTTP {(int)response.StatusCode}. Check authentication, account permissions and request requirements.", code, response.StatusCode);
    }

    public static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return new JsonObject();
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            throw new SdkException("Expected JSON. An HTML login or access challenge may have been returned.", "unexpected_content_type");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new SdkException("Endpoint returned empty JSON.", "empty_response");
    }

    public static async Task<JsonNode?> ReadNullableJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            throw new SdkException("Expected JSON. An HTML login or access challenge may have been returned.", "unexpected_content_type");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // JSON null is a valid optional backend result, distinct from an empty or malformed response body.
        return await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
