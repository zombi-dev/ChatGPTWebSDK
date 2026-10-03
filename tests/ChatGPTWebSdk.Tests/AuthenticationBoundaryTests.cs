using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class AuthenticationBoundaryTests
{
    private static JsonObject Payload()
    {
        var input = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/auth-export.json")))!["authenticationString"]!.GetValue<string>()[WebAuthentication.Prefix.Length..];
        return JsonNode.Parse(Convert.FromBase64String(input.Replace('-', '+').Replace('_', '/') + new string('=', (4 - input.Length % 4) % 4)))!.AsObject();
    }
    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Encode(JsonObject payload) => WebAuthentication.Prefix + Base64(payload.ToJsonString());

    public static IEnumerable<object[]> InvalidExports()
    {
        foreach (var origin in new[] { "https://chatgpt.com/", "https://chatgpt.com:443", "https://chatgpt.com.evil.test", "https://www.chatgpt.com", "https://chatgpt.com@evil.test", "https://chat.openai.com", "HTTPS://chatgpt.com", "https://chatgpt.com/path" })
            yield return ["origin", JsonValue.Create(origin)!.ToJsonString()];
        foreach (var json in new[] { "0", "-1", "null", "true", "\"1\"" }) yield return ["version", json];
        foreach (var json in new[] { "null", "false", "123", "\"\"", "\"not-a-date\"" }) yield return ["createdAt", json];
        foreach (var value in new[] { "", "\t", "agent\0suffix", "agent\u007fsuffix", new string('a', 2049) }) yield return ["userAgent", JsonValue.Create(value)!.ToJsonString()];
        foreach (var value in new[] { "", " ", "token\0suffix", "token\u007fsuffix" }) yield return ["accessToken", JsonValue.Create(value)!.ToJsonString()];
        foreach (var header in new[] { "Cookie", "Host", "Origin", "Referer", "Content-Length", "chatgpt-account-id", "X-Custom-Token", "openai-sentinel-turnstile-token" })
            yield return ["headers", new JsonObject { [header] = "synthetic-private-value" }.ToJsonString()];
        foreach (var json in new[] { "null", "[]", "{\"oai-language\":null}", "{\"oai-language\":\"en\",\"OAI-LANGUAGE\":\"fr\"}", "{\"oai-language\":\"\"}" }) yield return ["headers", json];
        yield return ["cookies", "{}"];
        yield return ["unknownSecret", "\"synthetic-private-value\""];
    }

    [Theory]
    [MemberData(nameof(InvalidExports))]
    public void Invalid_exports_fail_without_exposing_supplied_data(string field, string json)
    {
        var payload = Payload(); payload[field] = JsonNode.Parse(json);
        var error = Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(payload)));
        Assert.DoesNotContain("synthetic-private-value", error.ToString());
        Assert.Null(error.InnerException);
    }

    public static IEnumerable<object[]> InvalidCookies()
    {
        foreach (var name in new[] { "", "has space", "has\tcharacter", "colon:name", "quote\"name", "unicode-ñ", "[brackets]" }) yield return ["name", JsonValue.Create(name)!.ToJsonString()];
        foreach (var value in new[] { "bad\0value", "bad\tvalue", "bad\u007fvalue", "unicode-ñ", "first;second" }) yield return ["value", JsonValue.Create(value)!.ToJsonString()];
        foreach (var domain in new[] { "com", "sub.chatgpt.com", ".com", ".evil.test", "chatgpt.com.", "CHATGPT.COM" }) yield return ["domain", JsonValue.Create(domain)!.ToJsonString()];
        foreach (var path in new[] { "", "backend-api", "/bad\0path", "/bad\npath" }) yield return ["path", JsonValue.Create(path)!.ToJsonString()];
        foreach (var sameSite in new[] { "None", "Lax", "Strict", "none", "invalid" }) yield return ["sameSite", JsonValue.Create(sameSite)!.ToJsonString()];
        yield return ["hostOnly", "true"];
        yield return ["expiresAt", "\"not-a-date\""];
        yield return ["unexpected", "\"synthetic-private-value\""];
    }

    [Theory]
    [MemberData(nameof(InvalidCookies))]
    public void Invalid_cookie_boundaries_are_rejected(string field, string json)
    {
        var payload = Payload(); payload["cookies"]![0]![field] = JsonNode.Parse(json);
        var error = Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(payload)));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("null")] [InlineData("true")]
    [InlineData("{\"exp\":null}")] [InlineData("{\"exp\":\"1000\"}")] [InlineData("{\"exp\":true}")]
    [InlineData("{\"exp\":1.5}")] [InlineData("{\"exp\":9223372036854775808}")]
    [InlineData("{\"exp\":253402300800}")] [InlineData("{\"exp\":-62135596801}")]
    [InlineData("invalid-json")]
    public void Invalid_JWT_lifetime_hints_return_unknown(string json) =>
        Assert.Null(WebAuthentication.ReadAccessTokenExpiry("synthetic." + Base64(json) + ".signature"));

    [Theory]
    [InlineData(0L)] [InlineData(1L)] [InlineData(-1L)] [InlineData(2524608000L)]
    [InlineData(253402300799L)] [InlineData(-62135596800L)]
    public void Valid_JWT_lifetime_bounds_are_read_without_authenticating_the_token(long seconds) =>
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(seconds), WebAuthentication.ReadAccessTokenExpiry("synthetic." + Base64("{\"exp\":" + seconds + "}") + ".signature"));

    [Fact]
    public void Cookie_count_is_bounded()
    {
        var payload = Payload(); var cookie = payload["cookies"]![0]!.DeepClone();
        var cookies = new JsonArray();
        for (var i = 0; i < 257; i++) cookies.Add(cookie.DeepClone());
        payload["cookies"] = cookies;
        Assert.Throws<FormatException>(() => WebAuthentication.Import(Encode(payload)));
    }
}
