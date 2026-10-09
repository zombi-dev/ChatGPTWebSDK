using System.ClientModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;
using OpenAI.Chat;

namespace QuickStart;

/// <summary>Exercises the same clients as the interactive example without logging credentials or model output.</summary>
internal static class SmokeCheck
{
    private static readonly string[][] Families =
    [
        ["gpt-6", "gpt-6-thinking", "gpt-6-instant"],
        ["gpt-5-6", "gpt-5-6-thinking", "gpt-5-6-instant", "gpt-5.6-sol-wm"],
        ["gpt-5-5", "gpt-5-5-thinking", "gpt-5-5-instant", "gpt-5.5-wm"]
    ];

    internal static async Task<JsonObject> RunAsync(string? authentication, BrowserSentinelOptions browser, bool apiOnly = false,
        HttpClient? http = null, WebEndpointProfile? endpoints = null, CancellationToken ct = default)
    {
        var report = new JsonObject { ["schema"] = 1, ["status"] = "blocked", ["code"] = "authentication_missing", ["catalog"] = new JsonArray(), ["models"] = new JsonArray() };
        if (string.IsNullOrWhiteSpace(authentication)) return report;
        ChatGPTWebRuntime runtime;
        try
        {
            runtime = new(new()
            {
                Credentials = new StaticWebCredentialProvider("monitor", WebAuthentication.Import(authentication)), AccountId = "monitor", UserId = "daily-example",
                ConversationStore = new InMemoryConversationStore(), TemporaryChat = true, HttpClient = http,
                Mode = apiOnly ? ChatGPTWebMode.ApiOnly : ChatGPTWebMode.Hybrid, Browser = browser,
                Endpoints = endpoints ?? WebEndpointProfile.Modern, RequestTimeout = TimeSpan.FromMinutes(3)
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or SdkException)
        { report["code"] = "authentication_invalid"; return report; }
        using (runtime)
        {
            string[] slugs;
            try
            {
                // Raw discovery observes additions even when the default compatibility list restricts them.
                var raw = await runtime.Web.Transport.GetModelsAsync("monitor", ct);
                if (raw["models"] is not JsonArray array || array.Any(m => m?["slug"] is not JsonValue v || !v.TryGetValue<string>(out _)))
                    throw new SdkException("Unknown model schema.", "unsupported_models_format");
                slugs = array.Select(m => m!["slug"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (slugs.Length > 128 || slugs.Any(s => s.Length is < 1 or > 80 || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))))
                    throw new SdkException("Unknown model identifiers.", "unsupported_models_format");
                report["catalog"] = new JsonArray(slugs.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                report["apiShape"] = ShapeHash(raw);
                // Also check the public, official-compatible model client, not just the native transport.
                var compatible = await runtime.CreateClient().GetOpenAIModelClient().GetModelsAsync(ct);
                var expected = slugs.Where(runtime.Web.Transport.ModelPolicy.IsAllowed).Order(StringComparer.Ordinal);
                if (!compatible.Value.Select(m => m.Id).Order(StringComparer.Ordinal).SequenceEqual(expected))
                    throw new SdkException("Model client disagrees with discovery.", "models_adapter_mismatch");
            }
            catch (Exception ex)
            { SetFailure(report, ex); return report; }
            var selected = Families.Select(f => f.FirstOrDefault(s => slugs.Contains(s) && runtime.Web.Transport.ModelPolicy.IsAllowed(s))).OfType<string>().ToArray();
            if (selected.Length == 0)
            { report["status"] = slugs.Length == 0 ? "blocked" : "failed"; report["code"] = slugs.Length == 0 ? "account_models_unavailable" : "no_supported_models"; return report; }
            foreach (var model in selected)
            {
                var entry = new JsonObject { ["model"] = model, ["status"] = "passed", ["code"] = "ok" };
                ((JsonArray)report["models"]!).Add(entry);
                if (ct.IsCancellationRequested) { entry["status"] = "blocked"; entry["code"] = "request_timeout"; continue; }
                try
                {
                    var marker = "SDKCHECK" + Guid.NewGuid().ToString("N");
                    var chat = runtime.CreateClient(threadId: "monitor-" + Guid.NewGuid().ToString("N"), temporaryChat: true).GetChatClient(model);
                    var first = await chat.CompleteChatAsync([new UserChatMessage("Remember this exact marker for my next question: " + marker + ". Reply with just the marker.")], cancellationToken: ct);
                    if (!string.Concat(first.Value.Content.Select(c => c.Text)).Contains(marker, StringComparison.Ordinal))
                        throw new SdkException("The first turn did not return the marker.", "example_first_turn_mismatch");
                    var response = new StringBuilder(); var updates = 0;
                    await foreach (var update in chat.CompleteChatStreamingAsync([new UserChatMessage("What was the exact marker? Reply with just that marker.")], cancellationToken: ct))
                    { updates++; foreach (var part in update.ContentUpdate) response.Append(part.Text); }
                    if (updates == 0 || !response.ToString().Contains(marker, StringComparison.Ordinal))
                        throw new SdkException("Streaming continuation did not remember the marker.", "example_stream_context_mismatch");
                    entry["streaming"] = true; entry["continuation"] = true;
                }
                catch (Exception ex) { SetFailure(entry, ex); }
            }
            var results = ((JsonArray)report["models"]!).OfType<JsonObject>().ToArray();
            var passed = results.Count(r => r["status"]!.GetValue<string>() == "passed");
            // A blocked model leaves the complete-breakage conclusion unproven, even if another failed.
            report["status"] = passed == results.Length ? "passed" : passed > 0 ? "degraded" :
                results.All(r => r["status"]!.GetValue<string>() == "failed") ? "failed" : "blocked";
            report["code"] = report["status"]!.GetValue<string>() == "passed" ? "ok" : "example_checks_incomplete";
            return report;
        }
    }

    private static void SetFailure(JsonObject report, Exception error)
    {
        var code = error is SdkException sdk ? sdk.Code : error is ClientResultException result ? "http_" + result.Status :
            error is HttpRequestException ? "network_unavailable" : error is OperationCanceledException or TimeoutException ? "request_timeout" :
            error is System.Text.Json.JsonException or InvalidOperationException or FormatException ? "response_schema_changed" : "example_failure";
        if (error is ClientResultException clientError)
        {
            try
            {
                var raw = clientError.GetRawResponse()?.Content?.ToString();
                var backendCode = raw is null ? null : JsonNode.Parse(raw)?["error"]?["code"]?.GetValue<string>();
                if (backendCode is not null && backendCode.Length <= 80 && backendCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) code = backendCode;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException) { }
        }
        var blocked = code is "authentication_required" or "web_access_denied" or "web_challenge_required" or "expired_sentinel_session" or
            "rate_limit_exceeded" or "http_401" or "http_403" or "http_429" or "network_unavailable" or "request_timeout" or
            "browser_not_found" or "sentinel_browser_challenge" or "sentinel_browser_timeout" or "sentinel_browser_closed" or
            "sentinel_browser_unavailable" or "sentinel_hardware_unavailable" or "authentication_account_mismatch";
        // No exception message or response body belongs in a monitor artifact or issue.
        report["status"] = blocked ? "blocked" : "failed";
        var known = blocked || code is "unsupported_models_format" or "models_adapter_mismatch" or "response_schema_changed" or
            "example_first_turn_mismatch" or "example_stream_context_mismatch" or "example_failure" or "unsupported_web_feature" or
            "incomplete_web_response" or "unexpected_content_type" or "empty_response" or "model_restricted" or
            "sentinel_browser_failed" or "sentinel_headers_missing" or "sentinel_browser_cleanup_failed" or "conversation_reconciliation_required" ||
            code.Length == 8 && code.StartsWith("http_", StringComparison.Ordinal) && code[5..].All(char.IsAsciiDigit);
        report["code"] = known ? code : "example_failure";
    }

    internal static string ShapeHash(JsonNode value)
    {
        string Shape(JsonNode? node, int depth) => depth > 8 ? "depth" : node switch
        {
            JsonObject obj => "{" + string.Join(',', obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + Shape(p.Value, depth + 1))) + "}",
            JsonArray array => "[" + string.Join(',', array.Take(64).Select(n => Shape(n, depth + 1)).Distinct().Order(StringComparer.Ordinal)) + "]",
            JsonValue scalar => scalar.GetValueKind().ToString(), _ => "null"
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Shape(value, 0)))).ToLowerInvariant();
    }
}
