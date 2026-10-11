// OAuth flow adapted from EvanZhouDev/openai-oauth (Apache-2.0); see third_party/openai-oauth.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

public sealed class OAuthLoginRequest
{
    public required Uri AuthorizationUri { get; init; }
    public required Uri RedirectUri { get; init; }
    public required string State { get; init; }
    public required string CodeVerifier { get; init; }
    public override string ToString() => "OAuthLoginRequest { authorization state redacted }";
}

public sealed class OpenAIOAuthOptions
{
    public string ClientId { get; init; } = "app_EMoamEEZ73f0CkXaXp7hrann";
    public Uri Issuer { get; init; } = new("https://auth.openai.com/");
    public string Scope { get; init; } = "openid profile email offline_access";
}

/// <summary>PKCE sign-in and refresh using an application-owned HTTP client. Does not launch or automate a browser.</summary>
public sealed class OpenAIOAuthClient
{
    private readonly HttpClient _http;
    private readonly OpenAIOAuthOptions _options;
    public OpenAIOAuthClient(HttpClient http, OpenAIOAuthOptions? options = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http)); _options = options ?? new();
        ValidateEndpoint(_options.Issuer);
        if (string.IsNullOrWhiteSpace(_options.ClientId) || _options.ClientId.Any(char.IsControl)) throw new ArgumentException("Invalid OAuth client ID.");
    }
    internal static void ValidateEndpoint(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 ||
            (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")))
            throw new ArgumentException("OAuth endpoints require HTTPS, or HTTP on loopback, without credentials, fragments or query strings.");
    }
    private Uri Endpoint(string path) => new(_options.Issuer.AbsoluteUri.TrimEnd('/') + "/" + path);
    private static string UrlSafe(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public OAuthLoginRequest CreateLoginRequest(Uri? redirectUri = null)
    {
        redirectUri ??= new("http://localhost:1455/auth/callback"); ValidateEndpoint(redirectUri);
        var state = UrlSafe(RandomNumberGenerator.GetBytes(24)); var verifier = UrlSafe(RandomNumberGenerator.GetBytes(48));
        var fields = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = _options.ClientId, ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = _options.Scope, ["state"] = state, ["code_challenge"] = UrlSafe(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true"
        };
        return new() { AuthorizationUri = new(Endpoint("oauth/authorize") + "?" + string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)))),
            RedirectUri = redirectUri, State = state, CodeVerifier = verifier };
    }

    /// <summary>Validates the callback address and anti-CSRF state before exchanging the authorization code.</summary>
    public Task<OAuthSession> ExchangeCallbackAsync(OAuthLoginRequest login, Uri callback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(login); ArgumentNullException.ThrowIfNull(callback);
        if (!callback.IsAbsoluteUri || callback.UserInfo.Length > 0 || callback.Fragment.Length > 0 ||
            callback.GetLeftPart(UriPartial.Path) != login.RedirectUri.GetLeftPart(UriPartial.Path))
            throw new SdkException("OAuth callback address did not match the requested redirect.", "oauth_callback_invalid");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!fields.TryAdd(Uri.UnescapeDataString(parts[0]), parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : ""))
                throw new SdkException("OAuth callback contained repeated parameters.", "oauth_callback_invalid");
        }
        if (!fields.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(login.State)))
            throw new SdkException("OAuth callback state did not match.", "oauth_state_mismatch");
        if (fields.ContainsKey("error") || !fields.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new SdkException("OAuth sign-in was rejected or returned no authorization code.", "oauth_login_failed");
        return RequestAsync(new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = login.CodeVerifier,
            ["client_id"] = _options.ClientId, ["redirect_uri"] = login.RedirectUri.AbsoluteUri
        }), null, ct);
    }

    public Task<OAuthSession> RefreshAsync(OAuthSession session, CancellationToken ct = default)
    {
        session.Validate();
        if (string.IsNullOrWhiteSpace(session.RefreshToken)) throw new SdkException("OAuth refresh token is missing.", "oauth_login_required");
        return RequestAsync(new StringContent(JsonSerializer.Serialize(new { grant_type = "refresh_token", refresh_token = session.RefreshToken, client_id = _options.ClientId }),
            Encoding.UTF8, "application/json"), session, ct);
    }

    private async Task<OAuthSession> RequestAsync(HttpContent content, OAuthSession? previous, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("oauth/token")) { Content = content };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SdkException("OAuth token exchange failed. Sign in again if the refresh token was revoked.", "oauth_token_failed", response.StatusCode);
        try { return OAuthSession.Parse(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject ?? throw new JsonException(), previous); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new SdkException("The OAuth token endpoint returned an invalid response.", "oauth_response_invalid", HttpStatusCode.BadGateway); }
    }
}
