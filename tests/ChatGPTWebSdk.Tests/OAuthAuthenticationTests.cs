using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.OAuth;
using ChatGPTWebSdk.Protocol;
using Xunit;

namespace ChatGPTWebSdk.Tests;

public sealed class OAuthAuthenticationTests
{
    internal static string Jwt(string account, long? expires = null, bool fedRamp = false) => "synthetic." + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
    { ["exp"] = expires ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["https://api.openai.com/auth"] = new JsonObject
        { ["chatgpt_account_id"] = account, ["chatgpt_account_is_fedramp"] = fedRamp } }.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
    internal static OAuthSession Session(bool expired = false) => new() { AccessToken = Jwt("account"), AccountId = "account", RefreshToken = "synthetic-refresh",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 60) };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Calls); return send(request, ct); }
    }
    internal static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void PkceUsesCryptographicStateAndS256WithNoBrowser()
    {
        using var http = new HttpClient(); var client = new OpenAIOAuthClient(http);
        var a = client.CreateLoginRequest(); var b = client.CreateLoginRequest(); var fields = Query(a.AuthorizationUri);
        Assert.NotEqual(a.State, b.State); Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
        Assert.InRange(a.CodeVerifier.Length, 43, 128); Assert.Equal("S256", fields["code_challenge_method"]);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(a.CodeVerifier))).TrimEnd('=').Replace('+','-').Replace('/','_'), fields["code_challenge"]);
        Assert.Equal("http://localhost:1455/auth/callback", fields["redirect_uri"]);
        Assert.DoesNotContain(a.CodeVerifier, a.AuthorizationUri.AbsoluteUri); Assert.DoesNotContain(a.State, a.ToString());
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://user:secret@auth.example/")]
    [InlineData("https://auth.example/?query=1")]
    [InlineData("https://auth.example/#fragment")]
    [InlineData("file:///tmp/auth")]
    public void InsecureIssuerIsRejected(string address)
    { using var http = new HttpClient(); Assert.Throws<ArgumentException>(() => new OpenAIOAuthClient(http, new() { Issuer = new(address) })); }

    [Theory]
    [InlineData("host")]
    [InlineData("path")]
    [InlineData("state")]
    [InlineData("duplicate")]
    [InlineData("error")]
    [InlineData("missing-code")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    public async Task InvalidCallbacksNeverExchangeCredentials(string change)
    {
        using var handler = new Handler((_, _) => throw new Exception("Callback must be rejected locally.")); using var http = new HttpClient(handler);
        var client = new OpenAIOAuthClient(http); var login = client.CreateLoginRequest();
        var callback = login.RedirectUri + "?state=" + login.State + "&code=synthetic-code";
        callback = change switch
        {
            "host" => callback.Replace("localhost", "example.com"), "path" => callback.Replace("/auth/callback", "/wrong"),
            "state" => callback.Replace(login.State, "wrong"), "duplicate" => callback + "&state=" + login.State,
            "error" => callback + "&error=denied", "missing-code" => callback.Replace("&code=synthetic-code", ""),
            "fragment" => callback + "#code=hidden", "userinfo" => callback.Replace("http://", "http://user@"), _ => callback
        };
        await Assert.ThrowsAsync<SdkException>(async () => await client.ExchangeCallbackAsync(login, new(callback)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallbackExchangesFormCodeAndDerivesAccount()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync(ct); Assert.Contains("code_verifier=", body); Assert.Contains("code=synthetic-code", body);
            return Json(new() { ["access_token"] = Jwt("account", fedRamp:true), ["refresh_token"] = "synthetic-new", ["expires_in"] = 3600 });
        });
        using var http = new HttpClient(handler); var client = new OpenAIOAuthClient(http); var login = client.CreateLoginRequest();
        var session = await client.ExchangeCallbackAsync(login, new(login.RedirectUri + "?state=" + login.State + "&code=synthetic-code"));
        Assert.Equal("account", session.AccountId); Assert.True(session.IsFedRamp); Assert.NotNull(session.ExpiresAt);
        Assert.DoesNotContain(session.AccessToken, session.ToString());
    }

    [Fact]
    public async Task ConcurrentRefreshRotatesOnceAndPersistsBeforeReturning()
    {
        var persisted = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            await Task.Delay(30, ct); Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)); Assert.Equal("refresh_token", body!["grant_type"]!.GetValue<string>());
            return Json(new() { ["access_token"] = Jwt("account"), ["refresh_token"] = "synthetic-rotated", ["expires_in"] = 3600 });
        });
        using var http = new HttpClient(handler);
        var provider = new RefreshingOAuthSessionProvider(Session(true), new(http), (session, _) => { Assert.Equal("synthetic-rotated", session.RefreshToken); persisted++; return Task.CompletedTask; });
        var sessions = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ => await provider.GetSessionAsync()));
        Assert.Equal(1, handler.Calls); Assert.Equal(1, persisted); Assert.All(sessions, s => Assert.Same(sessions[0], s));
    }

    [Fact]
    public async Task RefreshCannotSwitchAccounts()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new() { ["access_token"] = Jwt("other-account") })));
        using var http = new HttpClient(handler); var provider = new RefreshingOAuthSessionProvider(Session(true), new(http));
        var error = await Assert.ThrowsAsync<SdkException>(async () => await provider.GetSessionAsync()); Assert.Equal("oauth_account_mismatch", error.Code);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task OAuthErrorsNeverExposeResponseSecrets(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new() { ["error_description"] = "private-synthetic-secret" }, (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SdkException>(() => new OpenAIOAuthClient(http).RefreshAsync(Session()));
        Assert.DoesNotContain("private-synthetic-secret", error.ToString()); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AuthFileRoundTripIsExplicitAtomicAndPreservesUnknownFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-oauth-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,"private.auth.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"custom\":\"keep\",\"tokens\":{\"custom_token_field\":true}}");
            await Session().SaveCodexAuthFileAsync(path);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(path)); Assert.Equal("keep", saved!["custom"]!.GetValue<string>());
            Assert.True(saved["tokens"]!["custom_token_field"]!.GetValue<bool>());
            var loaded = await OAuthSession.LoadCodexAuthFileAsync(path); Assert.Equal("account", loaded.AccountId); Assert.Equal("synthetic-refresh", loaded.RefreshToken);
            Assert.Single(Directory.GetFiles(directory));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FreshSessionDoesNotContactAuthServer()
    {
        using var handler = new Handler((_, _) => throw new Exception("Fresh credentials must not refresh.")); using var http = new HttpClient(handler);
        var session = Session(); Assert.Same(session, await new RefreshingOAuthSessionProvider(session, new(http)).GetSessionAsync()); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task FailedPersistenceRetriesSavingRotatedTokensWithoutRefreshingAgain()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new() { ["access_token"] = Jwt("account"), ["refresh_token"] = "synthetic-rotated", ["expires_in"] = 3600 })));
        using var http = new HttpClient(handler); var saves = 0;
        var provider = new RefreshingOAuthSessionProvider(Session(true), new(http), (_, _) => ++saves == 1 ? Task.FromException(new IOException("Synthetic storage failure")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(async () => await provider.GetSessionAsync());
        Assert.Equal("synthetic-rotated", (await provider.GetSessionAsync()).RefreshToken); Assert.Equal(1, handler.Calls); Assert.Equal(2, saves);
    }

    [Fact]
    public async Task StaleIdTokenCannotHideAnAccountSwitchDuringRefresh()
    {
        var old = new OAuthSession { AccessToken = Jwt("account"), AccountId = "account", IdToken = Jwt("account"), RefreshToken = "synthetic-refresh" };
        using var handler = new Handler((_, _) => Task.FromResult(Json(new() { ["access_token"] = Jwt("other-account") })));
        using var http = new HttpClient(handler);
        Assert.Equal("oauth_account_mismatch", (await Assert.ThrowsAsync<SdkException>(() => new OpenAIOAuthClient(http).RefreshAsync(old))).Code);
    }

    [Fact]
    public async Task ApiKeyOnlyFileIsNotImportedAsOAuth()
    {
        var path = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(path, "{\"OPENAI_API_KEY\":\"synthetic-key\"}"); await Assert.ThrowsAsync<SdkException>(() => OAuthSession.LoadCodexAuthFileAsync(path)); }
        finally { File.Delete(path); }
    }
}
