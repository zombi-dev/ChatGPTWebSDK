// In-memory replay adapted from EvanZhouDev/openai-oauth; Apache-2.0 notices are retained.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

internal sealed class CodexResponseHistory(CodexOAuthOptions options)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (JsonArray Input, JsonArray Output)> _responses = new(StringComparer.Ordinal);
    private long _characters;
    internal void Clear() { lock (_sync) { _responses.Clear(); _characters = 0; } }
    private void Put(string key, JsonArray input, JsonArray output)
    {
        if (_responses.Remove(key, out var old)) _characters -= old.Input.ToJsonString().Length + old.Output.ToJsonString().Length;
        _responses[key] = (input, output); _characters += input.ToJsonString().Length + output.ToJsonString().Length;
    }
    internal static string Key(string scope, string id) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope, id })));
    internal JsonObject Expand(string scope, JsonObject body)
    {
        var result = (JsonObject)body.DeepClone();
        var input = body["input"] is JsonArray array ? (JsonArray)array.DeepClone() : new JsonArray();
        lock (_sync)
        {
            if (body["previous_response_id"] is JsonValue previous)
            {
                if (!options.CacheResponseHistory || !_responses.TryGetValue(Key(scope, previous.GetValue<string>()), out var history))
                    throw new SdkException("Previous Codex response is unavailable in this account/user/thread cache. Supply complete input history.", "codex_history_missing");
                input = new JsonArray(history.Input.Concat(history.Output).Concat(input).Select(n => n?.DeepClone()).ToArray());
            }
            for (var i = 0; i < input.Count; i++)
            {
                if (input[i]?["type"]?.GetValue<string>() != "item_reference") continue;
                var id = input[i]!["id"]?.GetValue<string>();
                var item = id is not null && _responses.TryGetValue("item:" + Key(scope, id), out var saved) ? saved.Output[0] : null;
                if (item is null) throw new SdkException("Referenced Codex item is unavailable in this account/user/thread cache.", "codex_history_missing");
                input[i] = item.DeepClone();
            }
        }
        result.Remove("previous_response_id"); result["input"] = input;
        if (result.ToJsonString().Length > options.MaxContextCharacters) throw new SdkException("Codex input history exceeds its configured limit.", "codex_context_too_large");
        return result;
    }
    internal void Remember(string scope, JsonObject request, JsonObject response)
    {
        if (!options.CacheResponseHistory || response["status"]?.GetValue<string>() != "completed" || response["id"]?.GetValue<string>() is not { } id) return;
        var input = (JsonArray)request["input"]!.DeepClone(); var output = response["output"] is JsonArray items ? (JsonArray)items.DeepClone() : new();
        if (input.ToJsonString().Length + output.ToJsonString().Length > options.MaxContextCharacters) return;
        lock (_sync)
        {
            Put(Key(scope, id), input, output);
            foreach (var item in output)
                if (item?["id"]?.GetValue<string>() is { } itemId)
                Put("item:" + Key(scope, itemId), new(), new JsonArray(item.DeepClone()));
            while (_responses.Count > options.MaxCachedResponses || _characters > (long)options.MaxContextCharacters * 4)
            { var key = _responses.Keys.First(); var value = _responses[key]; _responses.Remove(key); _characters -= value.Input.ToJsonString().Length + value.Output.ToJsonString().Length; }
        }
    }
}
