using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

/// <summary>Obtains fresh Sentinel answers over HTTP without launching or executing a browser.</summary>
public sealed class HttpSentinelProvider : IWebSentinelSessionProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly WebClientOptions _options;
    private readonly ConcurrentDictionary<string, Workspace> _accounts = new(StringComparer.Ordinal);
    private bool _disposed;
    private sealed class Workspace
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal string? Source;
        internal string? Current;
        internal WebCredentials? Credentials;
        internal string Build = "";
        internal string IpInfo = "";
        internal string SessionId = "";
        internal DateTimeOffset Loaded;
        internal long Started;
    }
    public HttpSentinelProvider(HttpClient http, WebClientOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new();
        HttpProtocol.RelativeUri(_options.BaseUri, _options.Endpoints.Requirements);
    }
    /// <summary>Caches page metadata and cookies only. Generation proofs are always obtained per turn.</summary>
    public async Task WarmUpAsync(string accountId, WebCredentials credentials, CancellationToken ct = default)
    {
        var workspace = Account(accountId);
        await workspace.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadAsync(workspace, credentials, ct).ConfigureAwait(false); }
        finally { workspace.Gate.Release(); }
    }
    public async ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(turnBody);
        var workspace = Account(accountId);
        await workspace.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(workspace, credentials, ct).ConfigureAwait(false);
            var value = workspace.Credentials!;
            var configuration = Configuration(workspace, value, turnBody);
            var p = HttpSentinelProof.Requirements(configuration);
            var requirements = await RequirementsAsync(value, new JsonObject { ["p"] = p }, ct).ConfigureAwait(false);
            string? proof = null, turnstile = null;
            if (Required(requirements, "proofofwork"))
                proof = HttpSentinelProof.Solve(Text(requirements["proofofwork"], "seed"), Text(requirements["proofofwork"], "difficulty"), configuration, ct);
            if (Required(requirements, "turnstile"))
                turnstile = HttpSentinelVm.Decode(Text(requirements["turnstile"], "dx"), p, workspace.IpInfo, value.UserAgent, _options.BaseUri, ct);
            // The legacy endpoint accepts proof/Turnstile without browser observer telemetry.
            if (Required(requirements, "arkose"))
                throw new SdkException("This Sentinel challenge requires an external answer. Select Hybrid or configure ExternalApi.", "unsupported_http_sentinel_challenge");
            if (requirements["token"] is null && requirements["prepare_token"] is not null && _options.Endpoints.RequirementsFinalize is { } finalize)
            {
                var body = new JsonObject { ["prepare_token"] = requirements["prepare_token"]!.DeepClone() };
                if (proof is not null) body["proofofwork"] = proof;
                if (turnstile is not null) body["turnstile"] = turnstile;
                requirements = await SendJsonAsync(value, finalize, body, ct).ConfigureAwait(false);
            }
            var ttl = requirements["expire_after"] is JsonValue expiry && expiry.TryGetValue<double>(out var seconds) && double.IsFinite(seconds)
                ? Math.Clamp(seconds, 1, 300) : 120;
            var elapsed = Stopwatch.GetElapsedTime(workspace.Started).TotalMilliseconds;
            return new(value, new()
            {
                Token = Text(requirements, "token"), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl), ProofToken = proof, TurnstileToken = turnstile,
                EchoLogs = string.Create(CultureInfo.InvariantCulture, $"0,{(long)elapsed},1,{(long)elapsed + 1}")
            });
        }
        finally { workspace.Gate.Release(); }
    }
    private Workspace Account(string accountId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Specify an account ID.", nameof(accountId));
        return _accounts.GetOrAdd(accountId, static _ => new());
    }
    private async Task<JsonNode> RequirementsAsync(WebCredentials value, JsonObject body, CancellationToken ct)
    {
        using var request = ChatGptWebTransport.BuildRequest(value, HttpMethod.Post, _options.Endpoints.Requirements, body, _options.BaseUri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Safe discovery fallback happens before any conversation send; generations are never retried.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone && _options.Endpoints.RequirementsPrepare is { } prepare)
            return await SendJsonAsync(value, prepare, body, ct).ConfigureAwait(false);
        return await JsonAsync(response, ct).ConfigureAwait(false);
    }
    private async Task<JsonNode> SendJsonAsync(WebCredentials value, string path, JsonObject body, CancellationToken ct)
    {
        using var request = ChatGptWebTransport.BuildRequest(value, HttpMethod.Post, path, body, _options.BaseUri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await JsonAsync(response, ct).ConfigureAwait(false);
    }
    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            throw new SdkException("Sentinel did not return JSON.", "unexpected_content_type");
        try { return JsonNode.Parse(await ReadAsync(response, ct).ConfigureAwait(false), documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new SdkException("Sentinel returned an unsupported response format.", "unsupported_http_sentinel_challenge"); }
    }
    private async Task LoadAsync(Workspace workspace, WebCredentials source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fingerprint = Fingerprint(source);
        if ((workspace.Source == fingerprint || workspace.Current == fingerprint) && workspace.Loaded > DateTimeOffset.UtcNow.AddMinutes(-30)) return;
        // Public build/location metadata needs no account credentials; keep session cookies on authenticated endpoints.
        var userAgent = source.UserAgent.StartsWith("ChatGPTWebSdk/", StringComparison.Ordinal)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36" : source.UserAgent;
        using var request = new HttpRequestMessage(HttpMethod.Get, HttpProtocol.RelativeUri(_options.BaseUri, "."));
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await HttpProtocol.EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var html = await ReadAsync(response, ct).ConfigureAwait(false);
        var build = Regex.Match(html, "data-build=[\"']([^\"']{1,256})[\"']", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!build.Success) throw new SdkException("The ChatGPT page no longer exposes its client build identifier.", "unsupported_http_sentinel_page");
        var cookies = MergeCookies(source, response);
        var headers = new Dictionary<string, string>(source.Headers, StringComparer.OrdinalIgnoreCase) { ["oai-client-version"] = build.Groups[1].Value };
        if (!headers.ContainsKey("oai-device-id"))
            headers["oai-device-id"] = cookies.FirstOrDefault(cookie => cookie.Name == "oai-did")?.Value ?? Guid.NewGuid().ToString();
        headers.TryAdd("oai-language", "en-US"); headers.TryAdd("accept-language", "en-US,en;q=0.9");
        workspace.Credentials = new()
        {
            AccessToken = source.AccessToken, ExpiresAt = source.ExpiresAt, Cookies = cookies, Headers = headers,
            CookieHeader = source.CookieHeader, UserAgent = userAgent
        };
        var geo = new[] { "cfConnectingIp", "cfIpCity", "userRegion", "cfIpLatitude", "cfIpLongitude" }.Select(name => PageString(html, name)).ToArray();
        workspace.IpInfo = geo.All(value => value is not null) ? "[" + string.Join(", ", geo.Select(value => "'" + value!.Replace("\\", "\\\\").Replace("'", "\\'") + "'")) + "]" : "";
        workspace.Build = build.Groups[1].Value; workspace.SessionId = Guid.NewGuid().ToString();
        workspace.Loaded = DateTimeOffset.UtcNow; workspace.Started = Stopwatch.GetTimestamp();
        workspace.Source = fingerprint; workspace.Current = Fingerprint(workspace.Credentials);
    }
    private IReadOnlyList<WebCookie> MergeCookies(WebCredentials source, HttpResponseMessage response)
    {
        var cookies = source.Cookies.GroupBy(cookie => (cookie.Name, cookie.Domain.TrimStart('.'), cookie.Path)).ToDictionary(group => group.Key, group => group.Last());
        var jar = new CookieContainer();
        if (source.Cookies.Count == 0 && source.CookieHeader is not null)
            foreach (var cookie in source.CookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                try { jar.SetCookies(_options.BaseUri, cookie); } catch (CookieException) { throw new ArgumentException("Invalid account cookie header."); }
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values)
                try { jar.SetCookies(_options.BaseUri, value); } catch (CookieException) { throw new SdkException("ChatGPT returned an invalid cookie.", "unsupported_http_sentinel_page"); }
        foreach (Cookie cookie in jar.GetAllCookies())
            cookies[(cookie.Name, cookie.Domain.TrimStart('.'), cookie.Path)] = new()
            {
                Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path,
                HostOnly = !cookie.Domain.StartsWith('.'), Secure = cookie.Secure, HttpOnly = cookie.HttpOnly,
                ExpiresAt = cookie.Expires == DateTime.MinValue ? null : new DateTimeOffset(cookie.Expires.ToUniversalTime())
            };
        return cookies.Values.ToArray();
    }
    private static JsonArray Configuration(Workspace workspace, WebCredentials value, JsonObject body)
    {
        var timezone = body["timezone"]?.GetValue<string>() ?? TimeZoneInfo.Local.Id;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var offset = now.Offset;
        var date = now.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture) + " (" + zone.StandardName + ")";
        var language = value.Headers.GetValueOrDefault("oai-language", "en-US");
        return new(4880, date, 4294705152L, 1, value.UserAgent, null, workspace.Build, language, language + ",en", 0,
            "platform−" + (value.UserAgent.Contains("Windows", StringComparison.Ordinal) ? "Win32" : value.UserAgent.Contains("Mac", StringComparison.Ordinal) ? "MacIntel" : "Linux x86_64"),
            "location", "onload", Stopwatch.GetElapsedTime(workspace.Started).TotalMilliseconds, workspace.SessionId, "", 8, workspace.Loaded.ToUnixTimeMilliseconds());
    }
    private static string Fingerprint(WebCredentials value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        value.AccessToken + "\n" + value.UserAgent + "\n" + value.CookieHeader + "\n" + JsonSerializer.Serialize(value.Cookies) + "\n" +
        JsonSerializer.Serialize(value.Headers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)))));
    private static string? PageString(string html, string name)
    {
        var match = Regex.Match(html, "[\"']?" + name + "[\"']?\\s*:\\s*\"((?:\\\\.|[^\"\\\\]){0,256})\"", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return null;
        try { return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\""); }
        catch (JsonException) { return null; }
    }
    private static bool Required(JsonNode requirements, string name)
    {
        if (requirements[name] is null) return false;
        if (requirements[name] is not JsonObject challenge) throw new SdkException("Unsupported Sentinel challenge schema.", "unsupported_http_sentinel_challenge");
        if (challenge["required"] is null) return false;
        return challenge["required"] is JsonValue value && value.TryGetValue<bool>(out var required) ? required :
            throw new SdkException("Unsupported Sentinel challenge schema.", "unsupported_http_sentinel_challenge");
    }
    private static string Text(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 512 * 1024
        ? text : throw new SdkException("Sentinel response is missing a supported required field.", "unsupported_http_sentinel_challenge");
    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > 4 * 1024 * 1024) throw new SdkException("Sentinel metadata exceeded its size limit.", "http_sentinel_response_too_large");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
    public void Dispose() { _disposed = true; _accounts.Clear(); }
}
