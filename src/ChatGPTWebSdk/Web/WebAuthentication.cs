using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatGPTWebSdk.Web;

/// <summary>A ChatGPT cookie exported by the browser extension, with its original scope and flags.</summary>
public sealed class WebCookie
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Domain { get; init; }
    public string Path { get; init; } = "/";
    public bool HostOnly { get; init; }
    public bool Secure { get; init; }
    public bool HttpOnly { get; init; }
    public string SameSite { get; init; } = "unspecified";
    public DateTimeOffset? ExpiresAt { get; init; }
    public override string ToString() => "[WebCookie redacted]";
}

/// <summary>Imports the single clipboard string produced by the ChatGPT Web SDK Auth extension.</summary>
public static class WebAuthentication
{
    public const string Prefix = "cgweb1.";
    public const int MaxExportCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "oai-device-id", "oai-language", "accept-language", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform"
    };

    public static WebCredentials Import(string authenticationString)
    {
        ArgumentNullException.ThrowIfNull(authenticationString);
        // Do not include the supplied string or deserializer exception in an error: both can contain credentials.
        try
        {
            if (authenticationString.Length > MaxExportCharacters) throw Invalid();
            var input = authenticationString.Trim();
            if (!input.StartsWith(Prefix, StringComparison.Ordinal)) throw Invalid();
            var json = Decode(input[Prefix.Length..]);
            var export = JsonSerializer.Deserialize<AuthenticationExport>(json, JsonOptions) ?? throw Invalid();
            if (export.Version != 1 || export.Origin != "https://chatgpt.com" || export.CreatedAt == default
                || !HeaderValue(export.UserAgent) || export.UserAgent.Length > 2048
                || string.IsNullOrWhiteSpace(export.AccessToken) || !HeaderValue(export.AccessToken)
                || export.Cookies is null || export.Cookies.Count > 256 || export.Headers is null) throw Invalid();
            foreach (var cookie in export.Cookies)
            {
                if (cookie is null || string.IsNullOrEmpty(cookie.Name) || cookie.Name.Any(c => !CookieName(c))
                    || cookie.Value is null || cookie.Value.Any(c => c < 0x20 || c > 0x7e || c == ';')
                    || cookie.Domain is not ("chatgpt.com" or ".chatgpt.com")
                    || cookie.Path is null || !cookie.Path.StartsWith('/') || !HeaderValue(cookie.Path)
                    || cookie.SameSite is not ("unspecified" or "no_restriction" or "lax" or "strict")
                    || cookie.HostOnly && cookie.Domain.StartsWith('.')) throw Invalid();
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in export.Headers)
                if (!AllowedHeaders.Contains(name) || !HeaderValue(value) || !headers.TryAdd(name, value)) throw Invalid();
            var cookies = export.Cookies.Where(c => c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow).ToArray();
            var jwtExpiry = ReadAccessTokenExpiry(export.AccessToken);
            var expiry = jwtExpiry is { } jwt && (export.ExpiresAt is null || jwt < export.ExpiresAt) ? jwt : export.ExpiresAt;
            return new()
            {
                AccessToken = export.AccessToken, ExpiresAt = expiry, UserAgent = export.UserAgent, Headers = headers, Cookies = cookies,
                CookieHeader = CookieHeader(cookies, new Uri("https://chatgpt.com/"))
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
        {
            throw Invalid();
        }
    }

    /// <summary>Reads the JWT expiry as an untrusted lifetime hint. It does not validate or authenticate a token.</summary>
    public static DateTimeOffset? ReadAccessTokenExpiry(string? token)
    {
        try
        {
            if (token is null || token.Length > MaxExportCharacters) return null;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            using var payload = JsonDocument.Parse(Decode(parts[1]), new() { MaxDepth = 32 });
            return payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException) { return null; }
    }

    internal static string CookieHeader(IEnumerable<WebCookie> cookies, Uri requestUri) => string.Join("; ", cookies
        .Where(c => (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow)
            && (!c.Secure || requestUri.Scheme == "https")
            && (c.HostOnly ? requestUri.Host == c.Domain : requestUri.Host == c.Domain.TrimStart('.') || requestUri.Host.EndsWith("." + c.Domain.TrimStart('.'), StringComparison.Ordinal))
            && (requestUri.AbsolutePath == c.Path || requestUri.AbsolutePath.StartsWith(c.Path, StringComparison.Ordinal)
                && (c.Path.EndsWith('/') || requestUri.AbsolutePath.Length > c.Path.Length && requestUri.AbsolutePath[c.Path.Length] == '/')))
        .OrderByDescending(c => c.Path.Length).Select(c => c.Name + "=" + c.Value));

    private static bool HeaderValue(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Any(c => c < 0x20 || c == 0x7f);
    private static bool CookieName(char c) => c is > (char)0x20 and < (char)0x7f && !"()<>@,;:\\\"/[]?={}".Contains(c);
    private static FormatException Invalid() => new("Invalid ChatGPT authentication export. Copy a fresh string using the ChatGPT Web SDK Auth extension.");
    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Invalid();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    }
    private sealed class AuthenticationExport
    {
        public required int Version { get; init; }
        public required string Origin { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string AccessToken { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public required string UserAgent { get; init; }
        public required Dictionary<string, string> Headers { get; init; }
        public required List<WebCookie> Cookies { get; init; }
    }
}
