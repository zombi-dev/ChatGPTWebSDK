using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Web;
using OpenAI;

namespace ChatGPTWebSdk.Tests;

public sealed class AuthenticationTests
{
    private static string Fixture => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/auth-export.json")))!
        ["authenticationString"]!.GetValue<string>();
    private static JsonObject Payload()
    {
        var body = Fixture[WebAuthentication.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        return JsonNode.Parse(Convert.FromBase64String(body + new string('=', (4 - body.Length % 4) % 4)))!.AsObject();
    }
    private static string Encode(JsonObject body) => WebAuthentication.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Imports_the_extensions_actual_synthetic_export_with_browser_cookie_flags()
    {
        var value = WebAuthentication.Import(" \r\n" + Fixture + "\n");
        Assert.Equal("Synthetic Firefox/150", value.UserAgent);
        Assert.Equal(DateTimeOffset.Parse("2050-01-01T00:00:00Z"), value.ExpiresAt);
        var cookie = Assert.Single(value.Cookies);
        Assert.True(cookie.HttpOnly); Assert.True(cookie.Secure); Assert.False(cookie.HostOnly);
        Assert.Equal(".chatgpt.com", cookie.Domain);
        Assert.Equal("__Secure-next-auth.session-token=synthetic-session", value.CookieHeader);
        Assert.Equal("en-GB", value.Headers["oai-language"]);
        Assert.DoesNotContain("synthetic-session", value.ToString());
        Assert.DoesNotContain("synthetic-session", cookie.ToString());
        var browser = BrowserSentinelProvider.ToBrowserCookie(cookie);
        Assert.True(browser.HttpOnly); Assert.True(browser.Secure); Assert.Equal(".chatgpt.com", browser.Domain);
        Assert.Equal(Microsoft.Playwright.SameSiteAttribute.Lax, browser.SameSite);
    }

    [Fact]
    public void One_string_initialization_defaults_to_hybrid_and_accepts_account_user_and_directory_options()
    {
        using var runtime = ChatGPTWeb.Initialize(Fixture, "unused-synthetic-directory", "alice", accountId: "account");
        Assert.Equal(ChatGPTWebMode.Hybrid, runtime.Mode);
        Assert.Equal("account", runtime.ResolveScope(runtime.ClientKey).AccountId);
        Assert.Equal("alice", runtime.ResolveScope(runtime.ClientKey).UserId);
        Assert.NotNull(runtime.CreateClient().GetChatClient("synthetic-model"));
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("origin", "\"https://evil.test\"")]
    [InlineData("origin", "\"http://chatgpt.com\"")]
    [InlineData("accessToken", "\"secret\\r\\nInjected: yes\"")]
    [InlineData("userAgent", "\"agent\\nsecret\"")]
    [InlineData("createdAt", "\"invalid secret\"")]
    [InlineData("headers", "{\"Authorization\":\"Bearer secret\"}")]
    [InlineData("headers", "{\"openai-sentinel-proof-token\":\"secret\"}")]
    [InlineData("headers", "{\"oai-language\":\"en\\r\\nsecret\"}")]
    [InlineData("cookies", "[{\"name\":\"session\",\"value\":\"secret\",\"domain\":\"evil.test\"}]")]
    [InlineData("cookies", "[{\"name\":\"bad;name\",\"value\":\"secret\",\"domain\":\"chatgpt.com\"}]")]
    [InlineData("cookies", "[{\"name\":\"session\",\"value\":\"secret; injected\",\"domain\":\"chatgpt.com\"}]")]
    [InlineData("cookies", "null")]
    public void Malformed_export_errors_are_redacted(string property, string json)
    {
        var body = Payload(); body[property] = JsonNode.Parse(json);
        var error = Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(body)));
        Assert.DoesNotContain("secret", error.ToString()); Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cgweb2.secret")]
    [InlineData("cgweb1.secret\ninjected")]
    [InlineData("cgweb1.!!!!")]
    [InlineData("cgweb1.a")]
    public void Rejects_invalid_encoding_and_versions_with_a_redacted_error(string input) =>
        Assert.DoesNotContain("secret", Assert.Throws<FormatException>(() => WebAuthentication.Import(input)).ToString());

    [Fact]
    public void Oversized_strings_and_missing_required_properties_are_rejected()
    {
        Assert.Throws<FormatException>(() => WebAuthentication.Import(new string('a', WebAuthentication.MaxExportCharacters + 1)));
        var body = Payload(); body.Remove("userAgent");
        Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(body)));
    }

    [Fact]
    public async Task Cookie_paths_and_expiry_are_respected_by_HTTP_requests()
    {
        var body = Payload();
        body["cookies"]!.AsArray().Add(JsonNode.Parse("""{"name":"api-only","value":"synthetic-api","domain":"chatgpt.com","path":"/backend-api","httpOnly":true,"sameSite":"strict"}"""));
        body["cookies"]!.AsArray().Add(JsonNode.Parse("""{"name":"expired","value":"synthetic-expired","domain":"chatgpt.com","expiresAt":"2000-01-01T00:00:00Z"}"""));
        var value = WebAuthentication.Import(Encode(body));
        var requests = new List<(string Path, string Cookie)>();
        using var http = new HttpClient(new RecordingHandler((request, _) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, string.Join("; ", request.Headers.GetValues("Cookie"))));
            return Task.FromResult(FakeWebHandler.Json(new JsonObject()));
        }));
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", value));
        await transport.GetModelsAsync("account"); await transport.SendJsonAsync("account", HttpMethod.Get, "api/auth/session");
        Assert.Contains("api-only=synthetic-api", requests[0].Cookie);
        Assert.DoesNotContain("api-only", requests[1].Cookie);
        Assert.All(requests, request => Assert.DoesNotContain("expired", request.Cookie));
    }

    [Fact]
    public void Browser_cookie_JSON_values_are_preserved_without_allowing_header_injection()
    {
        var body = Payload();
        body["cookies"]![0]!["value"] = """{"value": "synthetic", "setting": true}""";
        var value = WebAuthentication.Import(Encode(body));
        Assert.Equal("""{"value": "synthetic", "setting": true}""", value.Cookies[0].Value);
        body["cookies"]![0]!["value"] = "synthetic\r\nCookie: injected";
        Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(body)));
    }

    [Fact]
    public async Task An_expired_export_refreshes_its_token_and_retains_structured_cookies()
    {
        var body = Payload(); body["expiresAt"] = "2000-01-01T00:00:00Z";
        var calls = 0;
        using var http = new HttpClient(new RecordingHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/api/auth/session")
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Contains("synthetic-session", string.Join("; ", request.Headers.GetValues("Cookie")));
                return Task.FromResult(FakeWebHandler.Json(new JsonObject { ["accessToken"] = "refreshed-synthetic-token", ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O") }));
            }
            Assert.Equal("refreshed-synthetic-token", request.Headers.Authorization!.Parameter);
            Assert.Contains("synthetic-session", string.Join("; ", request.Headers.GetValues("Cookie")));
            return Task.FromResult(FakeWebHandler.Json(new JsonObject()));
        }));
        var transport = new ChatGptWebTransport(http, new StaticWebCredentialProvider("account", WebAuthentication.Import(Encode(body))));
        await transport.GetModelsAsync("account"); await transport.GetModelsAsync("account");
        Assert.Equal(3, calls);
    }
}
