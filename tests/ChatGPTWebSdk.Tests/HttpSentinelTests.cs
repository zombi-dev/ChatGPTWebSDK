using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;

namespace ChatGPTWebSdk.Tests;

internal static class SentinelFixture
{
    internal const string Key = "synthetic-envelope";
    internal static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    internal static string Pack(JsonArray program, string token) => Encode(HttpSentinelVm.Xor(program.ToJsonString(), token));
    internal static JsonArray Program(string field = "location", bool alias = false)
    {
        var rows = new JsonArray();
        if (alias) rows.Add(new JsonArray(8, "assign", 2));
        var set = alias ? JsonValue.Create("assign")! : JsonValue.Create(2)!;
        void Assign(string register, JsonNode value) => rows.Add(new JsonArray(set.DeepClone(), register, value));
        Assign("setter", JsonValue.Create("set")!); Assign("object", new JsonObject());
        Assign("name", JsonValue.Create("answer")!); Assign("value", JsonValue.Create(field)!); Assign("key", JsonValue.Create(Key)!);
        rows.Add(new JsonArray(7, "setter", "object", "name", "value"));
        rows.Add(new JsonArray(15, "object")); rows.Add(new JsonArray(1, "object", "key")); rows.Add(new JsonArray(19, "object"));
        return rows;
    }
    internal static JsonObject Unpack(string proof) => JsonNode.Parse(HttpSentinelVm.Xor(Encoding.UTF8.GetString(Convert.FromBase64String(proof)), Key))!.AsObject();
    internal static string Unprotect(JsonNode value) => HttpSentinelVm.Xor(Encoding.UTF8.GetString(Convert.FromBase64String(value.GetValue<string>())), Key);
    internal static WebCredentials Credentials(string token = "synthetic-access", string language = "en-US") => new()
    {
        AccessToken = token, UserAgent = "Synthetic Firefox/157", Headers = new() { ["oai-language"] = language },
        Cookies = [new() { Name = "account-cookie", Value = token, Domain = "chatgpt.com", HostOnly = true, HttpOnly = true, Secure = true }]
    };
    internal const string Page = """<html data-build="synthetic-build"><script>{"cfConnectingIp":"192.0.2.1","cfIpCity":"Test City","userRegion":"Test Region","cfIpLatitude":"1.0","cfIpLongitude":"2.0"}</script></html>""";
    internal static HttpResponseMessage Html(string html = Page) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
}

public sealed class HttpSentinelProofTests
{
    [Theory]
    [InlineData("", "", 0xab3e7c0bu)] [InlineData("", "a", 0x1a80b1b3u)]
    [InlineData("he", "llo", 0x888d766eu)] [InlineData("fixture-seed", "fixture-payload", 0x12c3eddeu)]
    [InlineData("", "☃", 0xf809546cu)]
    public void Hash_matches_independent_protocol_vectors(string seed, string encoded, uint expected) => Assert.Equal(expected, HttpSentinelProof.Hash(seed, encoded));

    private static JsonArray Configuration() => new(4880, "synthetic-date", 4294705152L, 1, "synthetic-agent", null, "build", "en-US", "en-US,en", 0, "platform", "location", "onload", 1.5, "session", "", 8, 1234L);

    [Fact]
    public void Requirements_preserve_compact_Unicode_and_all_eighteen_fields()
    {
        var config = Configuration(); config[10] = "platform−Linux";
        var value = HttpSentinelProof.Requirements(config);
        Assert.StartsWith("gAAAAAC", value);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[7..]));
        Assert.Contains("platform−Linux", decoded); Assert.DoesNotContain("\\u", decoded);
        Assert.True(JsonNode.DeepEquals(config, JsonNode.Parse(decoded)));
    }

    [Theory]
    [InlineData("f")] [InlineData("F")] [InlineData("ff")] [InlineData("0")] [InlineData("00")] [InlineData("0a")] [InlineData("0A")]
    public void Proof_satisfies_the_server_hash_prefix_and_preserves_fixed_fields(string difficulty)
    {
        var original = Configuration(); var copy = original.DeepClone();
        var proof = HttpSentinelProof.Solve("synthetic-seed", difficulty, original, default);
        Assert.StartsWith("gAAAAAB", proof); Assert.EndsWith("~S", proof);
        var encoded = proof[7..^2];
        var hash = HttpSentinelProof.Hash("synthetic-seed", encoded).ToString("x8");
        Assert.True(string.CompareOrdinal(hash[..difficulty.Length], difficulty.ToLowerInvariant()) <= 0);
        var parsed = JsonNode.Parse(Convert.FromBase64String(encoded))!.AsArray();
        Assert.Equal(18, parsed.Count); Assert.InRange(parsed[3]!.GetValue<int>(), 0, 499999);
        Assert.InRange(parsed[9]!.GetValue<int>(), 0, int.MaxValue);
        for (var i = 0; i < 18; i++) if (i is not (3 or 9)) Assert.True(JsonNode.DeepEquals(original[i], parsed[i]));
        Assert.True(JsonNode.DeepEquals(original, copy));
    }

    [Theory]
    [InlineData("", "f")] [InlineData("seed", "")] [InlineData("seed", "g")] [InlineData("seed", "fffffffff")]
    [InlineData("seed", "-1")] [InlineData("seed", " f")] [InlineData("seed", "secret")]
    public void Invalid_work_parameters_are_rejected_without_echoing_values(string seed, string difficulty)
    {
        var error = Assert.Throws<SdkException>(() => HttpSentinelProof.Solve(seed, difficulty, Configuration(), default));
        Assert.Equal("unsupported_http_sentinel_challenge", error.Code); Assert.DoesNotContain("secret", error.Message);
    }
    [Fact]
    public void Proof_honors_cancellation_before_work() => Assert.Throws<OperationCanceledException>(() => HttpSentinelProof.Solve("seed", "0", Configuration(), new(true)));
    [Fact]
    public void Incorrect_configuration_length_is_rejected() => Assert.Throws<ArgumentException>(() => HttpSentinelProof.Solve("seed", "f", new(), default));
}

public sealed class HttpSentinelVmTests
{
    [Theory]
    [InlineData("location", "https://chatgpt.com/")]
    [InlineData("cfIpLongitude", "['192.0.2.1', 'City', 'Region', '1', '2']")]
    [InlineData("maxTouchPoints", "[\"\",\"Linux x86_64\",8,0]")]
    [InlineData("42", "42.0")]
    public void Synthetic_programs_produce_the_expected_protected_values(string field, string expected)
    {
        var answer = HttpSentinelVm.Decode(SentinelFixture.Pack(SentinelFixture.Program(field), "synthetic-p"), "synthetic-p",
            "['192.0.2.1', 'City', 'Region', '1', '2']", "Firefox/157", new("https://chatgpt.com/"));
        Assert.Equal(expected, SentinelFixture.Unprotect(SentinelFixture.Unpack(answer)["answer"]!));
    }

    [Theory]
    [InlineData("Windows Chrome/140", "[\"Google Inc.\",\"Win32\",8,0]")]
    [InlineData("Macintosh Chrome/140", "[\"Google Inc.\",\"MacIntel\",8,0]")]
    [InlineData("Linux Chrome/140", "[\"Google Inc.\",\"Linux x86_64\",8,0]")]
    [InlineData("Windows Firefox/157", "[\"\",\"Win32\",8,0]")]
    public void Vendor_values_match_the_exported_user_agent(string agent, string expected)
    {
        var answer = HttpSentinelVm.Decode(SentinelFixture.Pack(SentinelFixture.Program("maxTouchPoints"), "p"), "p", "", agent, new("https://chatgpt.com/"));
        Assert.Equal(expected, SentinelFixture.Unprotect(SentinelFixture.Unpack(answer)["answer"]!));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, true)]
    public void Decoder_handles_nested_programs_and_aliased_instructions(int layers, bool alias)
    {
        var program = SentinelFixture.Program(alias: alias);
        for (var i = 0; i < layers; i++) program = new(new JsonArray(23, "dummy", 2, "stage-key", "synthetic-stage"), new JsonArray(2, "nested", SentinelFixture.Pack(program, "synthetic-stage")));
        var answer = HttpSentinelVm.Decode(SentinelFixture.Pack(program, "p"), "p", "", "Firefox/157", new("https://chatgpt.com/"));
        Assert.Equal("https://chatgpt.com/", SentinelFixture.Unprotect(SentinelFixture.Unpack(answer)["answer"]!));
    }

    [Theory]
    [InlineData("history")] [InlineData("localStorage")] [InlineData("keys")] [InlineData("createElement")]
    public void Known_browser_metrics_are_data_without_executing_downloaded_code(string metric)
    {
        var proof = HttpSentinelVm.Decode(SentinelFixture.Pack(SentinelFixture.Program(metric), "p"), "p", "", "Firefox/157", new("https://chatgpt.com/"));
        var value = SentinelFixture.Unprotect(SentinelFixture.Unpack(proof)["answer"]!);
        if (metric == "history") Assert.InRange(int.Parse(value), 1, 5);
        else if (metric == "createElement") Assert.Equal(37.8125, JsonNode.Parse(value)!["width"]!.GetValue<double>());
        else Assert.Contains("oai-did", value);
    }

    [Theory]
    [InlineData(5, 7, 12)] [InlineData(33, 7, 35)] [InlineData(27, 3, 2)]
    public void Numeric_expressions_keep_arithmetic_values(int opcode, int operand, int expected)
    {
        var program = SentinelFixture.Program("5");
        program.Insert(5, new JsonArray(2, "operand", operand)); program.Insert(6, new JsonArray(opcode, "value", "operand"));
        var proof = HttpSentinelVm.Decode(SentinelFixture.Pack(program, "p"), "p", "", "Firefox/157", new("https://chatgpt.com/"));
        Assert.Equal(expected + ".0", SentinelFixture.Unprotect(SentinelFixture.Unpack(proof)["answer"]!));
    }

    [Theory]
    [InlineData("")] [InlineData("not base64 secret")] [InlineData("e30=")] [InlineData("W10=")]
    public void Invalid_programs_have_redacted_errors(string bytecode)
    {
        var error = Assert.Throws<SdkException>(() => HttpSentinelVm.Decode(bytecode, "p", "", "agent", new("https://chatgpt.com/")));
        Assert.Equal("unsupported_http_sentinel_challenge", error.Code); Assert.DoesNotContain("secret", error.ToString()); Assert.Null(error.InnerException);
    }
    [Fact]
    public void Missing_location_metadata_is_rejected_when_the_program_needs_it() => Assert.Throws<SdkException>(() => HttpSentinelVm.Decode(SentinelFixture.Pack(SentinelFixture.Program("cfIpLongitude"), "p"), "p", "", "agent", new("https://chatgpt.com/")));
    [Fact]
    public void Unknown_instructions_are_rejected_before_an_answer_is_returned()
    {
        var program = SentinelFixture.Program(); program.Insert(0, new JsonArray(999, "secret"));
        Assert.DoesNotContain("secret", Assert.Throws<SdkException>(() => HttpSentinelVm.Decode(SentinelFixture.Pack(program, "p"), "p", "", "agent", new("https://chatgpt.com/"))).ToString());
    }
    [Fact]
    public void Deep_shared_expressions_are_bounded_without_exponential_traversal()
    {
        var program = SentinelFixture.Program("1");
        for (var i = 0; i < 60; i++) program.Insert(5, new JsonArray(5, "value", "value"));
        Assert.Throws<SdkException>(() => HttpSentinelVm.Decode(SentinelFixture.Pack(program, "p"), "p", "", "agent", new("https://chatgpt.com/")));
    }
    [Fact]
    public void Encoded_program_size_is_bounded() => Assert.Throws<SdkException>(() => HttpSentinelVm.Decode(new string('a', 512 * 1024 + 1), "p", "", "agent", new("https://chatgpt.com/")));
    [Fact]
    public void Cancellation_is_observed_before_instructions() => Assert.Throws<OperationCanceledException>(() => HttpSentinelVm.Decode(SentinelFixture.Pack(SentinelFixture.Program(), "p"), "p", "", "agent", new("https://chatgpt.com/"), new(true)));
}

public sealed class HttpSentinelProviderTests
{
    private sealed class ChangingCredentials : IWebCredentialProvider
    {
        internal WebCredentials Value = SentinelFixture.Credentials();
        public ValueTask<WebCredentials> GetAsync(string accountId, CancellationToken ct = default) => ValueTask.FromResult(Value);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Pages, Requirements, Finalizes;
        internal string Page = SentinelFixture.Page;
        internal HttpStatusCode PageStatus = HttpStatusCode.OK;
        internal HttpStatusCode RequirementsStatus = HttpStatusCode.OK;
        internal JsonObject? Response;
        internal bool ModernFallback, RequireWork;
        internal string? SetCookie;
        internal readonly List<(string Token, string? Cookies, string? Build)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("chatgpt.com", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Get)
            {
                Pages++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
                var response = SentinelFixture.Html(Page); response.StatusCode = PageStatus;
                if (SetCookie is not null) response.Headers.TryAddWithoutValidation("Set-Cookie", SetCookie);
                return response;
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/finalize")) { Finalizes++; return FakeWebHandler.Json(new JsonObject { ["token"] = "synthetic-finalized" }); }
            Requirements++;
            Requests.Add((request.Headers.Authorization!.Parameter!, request.Headers.Contains("Cookie") ? string.Join(';', request.Headers.GetValues("Cookie")) : null,
                request.Headers.GetValues("oai-client-version").Single()));
            if (ModernFallback && request.RequestUri.AbsolutePath.EndsWith("chat-requirements")) return new(HttpStatusCode.NotFound);
            if (RequirementsStatus != HttpStatusCode.OK) return new(RequirementsStatus) { Content = new StringContent("private account secret") };
            if (Response is not null) return FakeWebHandler.Json(Response.DeepClone());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var p = body["p"]!.GetValue<string>(); Assert.StartsWith("gAAAAAC", p);
            var result = new JsonObject { ["token"] = "synthetic-token-" + Requirements, ["expire_after"] = 120, ["so"] = new JsonObject { ["required"] = true } };
            if (ModernFallback) { result.Remove("token"); result["prepare_token"] = "synthetic-prepare"; }
            if (RequireWork)
            {
                result["proofofwork"] = new JsonObject { ["required"] = true, ["seed"] = "synthetic-seed", ["difficulty"] = "f" };
                result["turnstile"] = new JsonObject { ["required"] = true, ["dx"] = SentinelFixture.Pack(SentinelFixture.Program("cfIpLongitude"), p) };
            }
            return FakeWebHandler.Json(result);
        }
    }
    [Fact]
    public async Task Warmup_caches_only_metadata_and_every_turn_gets_new_answers()
    {
        using var handler = new Handler { RequireWork = true }; using var http = new HttpClient(handler);
        using var provider = new HttpSentinelProvider(http);
        var credentials = SentinelFixture.Credentials();
        await provider.WarmUpAsync("account", credentials); Assert.Equal(1, handler.Pages); Assert.Equal(0, handler.Requirements);
        var first = await provider.GetSessionAsync("account", credentials, new());
        var next = await provider.GetSessionAsync("account", first.Credentials, new());
        Assert.Equal(1, handler.Pages); Assert.Equal(2, handler.Requirements); Assert.NotEqual(first.Sentinel.Token, next.Sentinel.Token);
        Assert.True(next.Sentinel.IsCurrent); Assert.NotNull(next.Sentinel.ProofToken); Assert.NotNull(next.Sentinel.TurnstileToken);
        Assert.Equal("['192.0.2.1', 'Test City', 'Test Region', '1.0', '2.0']", SentinelFixture.Unprotect(SentinelFixture.Unpack(next.Sentinel.TurnstileToken)["answer"]!));
        Assert.All(handler.Requests, request => { Assert.Equal("synthetic-access", request.Token); Assert.Contains("account-cookie=synthetic-access", request.Cookies!); Assert.Equal("synthetic-build", request.Build); });
    }
    [Fact]
    public async Task Accounts_have_separate_metadata_and_credentials()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        await provider.GetSessionAsync("alice", SentinelFixture.Credentials("alice"), new());
        await provider.GetSessionAsync("bob", SentinelFixture.Credentials("bob"), new());
        Assert.Equal(2, handler.Pages); Assert.Equal(new[] { "alice", "bob" }, handler.Requests.Select(r => r.Token));
    }
    [Theory]
    [InlineData("token")] [InlineData("headers")]
    public async Task Credential_changes_invalidate_account_metadata(string change)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var credentials = new ChangingCredentials();
        var transport = new ChatGptWebTransport(http, credentials, new() { SentinelSessionProvider = provider });
        using var first = await transport.OpenTurnAsync("account", new() { ["model"] = "gpt-6" });
        credentials.Value = SentinelFixture.Credentials(change == "token" ? "rotated" : "synthetic-access", change == "headers" ? "en-GB" : "en-US");
        using var next = await transport.OpenTurnAsync("account", new() { ["model"] = "gpt-6" });
        Assert.Equal(2, handler.Pages);
    }
    [Fact]
    public async Task Concurrent_account_preparations_share_one_bootstrap_but_never_one_token()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var credentials = SentinelFixture.Credentials();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetSessionAsync("account", credentials, new()).AsTask()));
        Assert.Equal(1, handler.Pages); Assert.Equal(8, results.Select(r => r.Sentinel.Token).Distinct().Count());
    }
    [Fact]
    public async Task Modern_discovery_falls_back_and_finalizes_before_generation()
    {
        using var handler = new Handler { ModernFallback = true, RequireWork = true }; using var http = new HttpClient(handler);
        using var provider = new HttpSentinelProvider(http, new() { Endpoints = WebEndpointProfile.Modern });
        var result = await provider.GetSessionAsync("account", SentinelFixture.Credentials(), new());
        Assert.Equal(2, handler.Requirements); Assert.Equal(1, handler.Finalizes); Assert.Equal("synthetic-finalized", result.Sentinel.Token);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task Requirements_failures_are_redacted_and_never_retried(int status)
    {
        using var handler = new Handler { RequirementsStatus = (HttpStatusCode)status }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var error = await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask());
        Assert.DoesNotContain("secret", error.ToString()); Assert.Equal(1, handler.Requirements);
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"token\":false}")] [InlineData("{\"token\":\"\"}")]
    [InlineData("{\"token\":\"synthetic\",\"arkose\":{\"required\":true}}")]
    [InlineData("{\"token\":\"synthetic\",\"turnstile\":{\"required\":\"secret\"}}")]
    [InlineData("{\"token\":\"synthetic\",\"proofofwork\":true}")]
    [InlineData("{\"token\":\"synthetic\",\"turnstile\":{\"required\":true}}")]
    public async Task Missing_or_changed_challenge_fields_do_not_yield_a_session(string json)
    {
        using var handler = new Handler { Response = JsonNode.Parse(json)!.AsObject() }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var error = await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask());
        Assert.Equal("unsupported_http_sentinel_challenge", error.Code); Assert.DoesNotContain("secret", error.ToString());
    }
    [Theory]
    [InlineData("<html>changed</html>", "unsupported_http_sentinel_page")]
    [InlineData("<html data-build=\"x\"></html>", null)]
    public async Task Page_changes_are_handled_without_third_party_location_requests(string html, string? code)
    {
        using var handler = new Handler { Page = html }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        if (code is null) Assert.NotNull(await provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()));
        else Assert.Equal(code, (await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask())).Code);
    }
    [Fact]
    public async Task Bootstrap_response_size_is_bounded()
    {
        using var handler = new Handler { Page = new string('a', 4 * 1024 * 1024 + 1) }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        Assert.Equal("http_sentinel_response_too_large", (await Assert.ThrowsAsync<SdkException>(() => provider.WarmUpAsync("account", SentinelFixture.Credentials()))).Code);
    }
    [Theory]
    [InlineData("/")] [InlineData("/backend-api")]
    public async Task Bootstrap_cookies_keep_their_flags_and_paths(string path)
    {
        using var handler = new Handler { SetCookie = "client-cookie=synthetic; Path=" + path + "; Secure; HttpOnly" }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var result = await provider.GetSessionAsync("account", SentinelFixture.Credentials(), new());
        var cookie = Assert.Single(result.Credentials.Cookies, c => c.Name == "client-cookie");
        Assert.Equal(path, cookie.Path); Assert.True(cookie.HttpOnly); Assert.True(cookie.Secure);
        Assert.Contains("client-cookie=synthetic", handler.Requests[0].Cookies!);
    }
    [Fact]
    public async Task Duplicate_equivalent_cookie_domains_do_not_break_bootstrap()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var credentials = new WebCredentials { AccessToken = "synthetic", Cookies = [new() { Name = "same", Value = "old", Domain = ".chatgpt.com" }, new() { Name = "same", Value = "current", Domain = "chatgpt.com" }] };
        var result = await provider.GetSessionAsync("account", credentials, new());
        Assert.Equal("current", Assert.Single(result.Credentials.Cookies).Value);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(500)]
    public async Task Bootstrap_failures_never_proceed_to_requirements(int status)
    {
        using var handler = new Handler { PageStatus = (HttpStatusCode)status }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask());
        Assert.Equal(0, handler.Requirements);
    }
    [Fact]
    public async Task Dispose_keeps_application_http_client_open_and_prevents_further_use()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); var provider = new HttpSentinelProvider(http);
        provider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.WarmUpAsync("account", SentinelFixture.Credentials()));
        using var response = await http.GetAsync("https://chatgpt.com/"); Assert.True(response.IsSuccessStatusCode);
    }
    [Fact]
    public async Task Canceled_warmup_sends_no_request()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.WarmUpAsync("account", SentinelFixture.Credentials(), new(true)));
        Assert.Equal(0, handler.Pages);
    }
    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("\n")]
    public async Task Empty_account_identifiers_are_rejected(string account)
    {
        using var http = new HttpClient(new Handler()); using var provider = new HttpSentinelProvider(http);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.WarmUpAsync(account, SentinelFixture.Credentials()));
    }
    [Theory]
    [InlineData("null", 120)] [InlineData("60", 60)] [InlineData("999", 300)] [InlineData("-1", 1)]
    public async Task Session_lifetimes_are_bounded_and_short_lifetimes_stay_unusable(string expiry, int seconds)
    {
        using var handler = new Handler { Response = new() { ["token"] = "synthetic", ["expire_after"] = JsonNode.Parse(expiry) } };
        using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        var result = await provider.GetSessionAsync("account", SentinelFixture.Credentials(), new());
        Assert.InRange((result.Sentinel.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, seconds - 2, seconds);
        Assert.Equal(seconds > 30, result.Sentinel.IsCurrent);
    }
    [Theory]
    [InlineData("text/html", "{}", "unexpected_content_type")]
    [InlineData("application/json", "[]", "unsupported_http_sentinel_challenge")]
    [InlineData("application/json", "private secret", "unsupported_http_sentinel_challenge")]
    [InlineData("application/json", "null", "unsupported_http_sentinel_challenge")]
    public async Task Invalid_HTTP_payloads_do_not_leak_response_bodies(string mediaType, string content, string code)
    {
        using var http = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? SentinelFixture.Html() :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, mediaType) })));
        using var provider = new HttpSentinelProvider(http);
        var error = await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask());
        Assert.Equal(code, error.Code); Assert.DoesNotContain("secret", error.ToString());
    }
    [Fact]
    public async Task Cloudflare_bootstrap_is_reported_without_a_generation_or_browser_fallback()
    {
        var requests = 0;
        using var http = new HttpClient(new RecordingHandler((_, _) =>
        {
            requests++; var response = new HttpResponseMessage(HttpStatusCode.Forbidden); response.Headers.Add("cf-mitigated", "challenge"); return Task.FromResult(response);
        }));
        using var provider = new HttpSentinelProvider(http);
        Assert.Equal("web_challenge_required", (await Assert.ThrowsAsync<WebChallengeException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask())).Code);
        Assert.Equal(1, requests);
    }
    [Fact]
    public async Task Missing_geolocation_only_blocks_challenges_that_need_it()
    {
        using var handler = new Handler { Page = "<html data-build=\"synthetic\"></html>", RequireWork = true }; using var http = new HttpClient(handler); using var provider = new HttpSentinelProvider(http);
        Assert.Equal("unsupported_http_sentinel_challenge", (await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", SentinelFixture.Credentials(), new()).AsTask())).Code);
    }
    [Fact]
    public void Configured_endpoint_cannot_escape_the_origin()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new HttpSentinelProvider(http, new() { Endpoints = new() { Requirements = "https://untrusted.test/secret" } }));
    }
    [Fact]
    public async Task New_default_runtime_streams_followups_with_account_bindings()
    {
        using var handler = new FakeWebHandler(); using var http = new HttpClient(handler);
        await using var runtime = new ChatGPTWebRuntime(new() { Credentials = new StaticWebCredentialProvider("account", SentinelFixture.Credentials()), AccountId = "account", HttpClient = http,
            ConversationStore = new InMemoryConversationStore(), Endpoints = new() });
        Assert.Equal(ChatGPTWebMode.ApiOnly, runtime.Mode);
        var chat = runtime.CreateClient().GetChatClient("gpt-6");
        await chat.CompleteChatAsync("first");
        var text = new StringBuilder();
        await foreach (var update in chat.CompleteChatStreamingAsync("followup")) foreach (var part in update.ContentUpdate) text.Append(part.Text);
        Assert.Equal("Hello world", text.ToString()); Assert.Equal(2, handler.Turns.Count);
        Assert.Equal("conversation-1", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.Single(handler.Turns[1].Body["messages"]!.AsArray());
    }
    [Theory]
    [InlineData(ChatGPTWebMode.ApiOnly, ChatGPTWebHttpDriver.DotNet)]
    [InlineData(ChatGPTWebMode.ExternalApi, ChatGPTWebHttpDriver.SystemCurl)]
    [InlineData(ChatGPTWebMode.Hybrid, ChatGPTWebHttpDriver.SystemCurl)]
    public void Default_http_driver_follows_mode_but_explicit_settings_win(ChatGPTWebMode mode, ChatGPTWebHttpDriver expected)
    {
        Assert.Equal(expected, new ChatGPTWebRuntimeOptions { Credentials = new StaticWebCredentialProvider("a", new()), Mode = mode }.HttpDriver);
        Assert.Equal(ChatGPTWebHttpDriver.DotNet, new ChatGPTWebRuntimeOptions { Credentials = new StaticWebCredentialProvider("a", new()), Mode = mode, HttpDriver = ChatGPTWebHttpDriver.DotNet }.HttpDriver);
    }
    [Fact]
    public void Existing_compiled_enum_values_keep_their_original_behavior()
    { Assert.Equal(0, (int)ChatGPTWebMode.ExternalApi); Assert.Equal(1, (int)ChatGPTWebMode.Hybrid); Assert.Equal(2, (int)ChatGPTWebMode.BrowserOnly); Assert.Equal(3, (int)ChatGPTWebMode.ApiOnly); }
}
