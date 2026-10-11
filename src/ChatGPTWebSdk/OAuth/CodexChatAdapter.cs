using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.OAuth;

internal static class CodexChatAdapter
{
    internal static JsonObject Request(JsonObject chat)
    {
        if (chat["n"] is not null && chat["n"]!.GetValue<int>() != 1 || chat["audio"] is not null || chat["functions"] is not null)
            throw new SdkException("Codex chat translation supports one text/image completion and modern function tools.", "codex_operation_unsupported");
        var input = new JsonArray();
        foreach (var message in (chat["messages"] as JsonArray ?? throw new ArgumentException("Chat messages are required.")).OfType<JsonObject>())
        {
            var role = message["role"]?.GetValue<string>() ?? throw new ArgumentException("Missing message role.");
            if (role == "tool")
            { input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message["tool_call_id"]?.DeepClone(), ["output"] = message["content"]?.DeepClone() }); continue; }
            if (role is not ("system" or "developer" or "user" or "assistant")) throw new ArgumentException("Unsupported chat role.");
            var parts = new JsonArray();
            if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text))
                parts.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text });
            else if (message["content"] is JsonArray content)
                foreach (var part in content)
                {
                    var type = part?["type"]?.GetValue<string>();
                    if (type == "text") parts.Add(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = part!["text"]?.DeepClone() });
                    else if (type == "image_url") parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = part!["image_url"]?["url"]?.DeepClone(), ["detail"] = part["image_url"]?["detail"]?.DeepClone() ?? "auto" });
                    else throw new SdkException("This chat content part has no Codex translation.", "codex_operation_unsupported");
                }
            if (parts.Count > 0) input.Add(new JsonObject { ["type"] = "message", ["role"] = role == "system" ? "developer" : role, ["content"] = parts });
            if (message["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call!["id"]?.DeepClone(),
                        ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = call["function"]?["arguments"]?.DeepClone() });
        }
        var result = new JsonObject { ["model"] = chat["model"]?.DeepClone(), ["input"] = input };
        foreach (var name in new[] { "temperature", "top_p", "parallel_tool_calls", "service_tier", "prompt_cache_key" })
            if (chat[name] is { } field) result[name] = field.DeepClone();
        if (chat["reasoning_effort"] is { } effort) result["reasoning"] = new JsonObject { ["effort"] = effort.DeepClone() };
        if (chat["tools"] is JsonArray tools)
            result["tools"] = new JsonArray(tools.Select(tool => tool?["type"]?.GetValue<string>() == "function" && tool["function"] is JsonObject function
                ? (JsonNode)new JsonObject(function.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))) { ["type"] = "function" }
                : throw new SdkException("Only function tools have a Chat Completions translation.", "codex_operation_unsupported")).ToArray());
        if (chat["tool_choice"] is JsonObject choice) result["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = choice["function"]?["name"]?.DeepClone() };
        else if (chat["tool_choice"] is { } selection) result["tool_choice"] = selection.DeepClone();
        if (chat["response_format"] is JsonObject format)
            result["text"] = new JsonObject { ["format"] = format["type"]?.GetValue<string>() == "json_schema" && format["json_schema"] is JsonObject schema
                ? new JsonObject(schema.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))) { ["type"] = "json_schema" } : format.DeepClone() };
        return result;
    }
    private static JsonObject Usage(JsonObject response)
    {
        var usage = response["usage"];
        return new() { ["prompt_tokens"] = usage?["input_tokens"]?.DeepClone() ?? 0, ["completion_tokens"] = usage?["output_tokens"]?.DeepClone() ?? 0,
            ["total_tokens"] = usage?["total_tokens"]?.DeepClone() ?? 0 };
    }
    private static string Finish(JsonObject response, bool tools) => tools ? "tool_calls" : response["status"]?.GetValue<string>() == "incomplete" ? "length" : "stop";
    internal static JsonObject Response(JsonObject response)
    {
        var text = ""; var calls = new JsonArray();
        foreach (var item in response["output"] as JsonArray ?? new())
        {
            if (item?["type"]?.GetValue<string>() == "function_call")
                calls.Add(new JsonObject { ["id"] = item["call_id"]?.DeepClone(), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = item["name"]?.DeepClone(), ["arguments"] = item["arguments"]?.DeepClone() } });
            else if (item?["content"] is JsonArray parts)
                foreach (var part in parts) if (part?["type"]?.GetValue<string>() == "output_text") text += part["text"]?.GetValue<string>();
        }
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 ? null : text };
        if (calls.Count > 0) message["tool_calls"] = calls;
        return new() { ["id"] = "chatcmpl_" + response["id"]?.GetValue<string>(), ["object"] = "chat.completion", ["created"] = response["created_at"]?.DeepClone() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = response["model"]?.DeepClone(), ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = Finish(response, calls.Count > 0) }), ["usage"] = Usage(response) };
    }

    internal static async IAsyncEnumerable<ServerSentEvent> Stream(IAsyncEnumerable<ServerSentEvent> events, string model, bool includeUsage, [EnumeratorCancellation] CancellationToken ct)
    {
        var id = "chatcmpl_" + Guid.NewGuid().ToString("N"); var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var calls = new Dictionary<string, int>(StringComparer.Ordinal); var arguments = new HashSet<string>(StringComparer.Ordinal);
        ServerSentEvent Chunk(JsonObject delta, string? finish = null, JsonObject? usage = null) => new(new JsonObject { ["id"] = id, ["object"] = "chat.completion.chunk",
            ["created"] = created, ["model"] = model, ["choices"] = usage is null ? new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }) : new JsonArray(), ["usage"] = usage }.ToJsonString());
        ServerSentEvent Tool(int index, JsonObject function, string? callId = null) => Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject
            { ["index"] = index, ["id"] = callId, ["type"] = callId is null ? null : "function", ["function"] = function }) });
        yield return Chunk(new() { ["role"] = "assistant" });
        await foreach (var item in events.WithCancellation(ct).ConfigureAwait(false))
        {
            var node = JsonNode.Parse(item.Data)!; var type = node["type"]?.GetValue<string>() ?? item.Event;
            if (type == "response.output_text.delta") yield return Chunk(new() { ["content"] = node["delta"]?.DeepClone() });
            else if (type is "response.output_item.added" or "response.output_item.done" && node["item"]?["type"]?.GetValue<string>() == "function_call")
            {
                var call = node["item"]!; var key = call["id"]!.GetValue<string>();
                if (!calls.TryGetValue(key, out var index))
                { index = calls.Count; calls.Add(key, index); yield return Tool(index, new() { ["name"] = call["name"]?.DeepClone(), ["arguments"] = "" }, call["call_id"]?.GetValue<string>()); }
                if (type == "response.output_item.done" && !arguments.Contains(key))
                { arguments.Add(key); yield return Tool(index, new() { ["arguments"] = call["arguments"]?.DeepClone() ?? "" }); }
            }
            else if (type == "response.function_call_arguments.delta")
            {
                var key = node["item_id"]!.GetValue<string>();
                if (!calls.TryGetValue(key, out var index)) throw new SdkException("Codex arguments arrived before the tool declaration.", "codex_stream_invalid");
                arguments.Add(key); yield return Tool(index, new() { ["arguments"] = node["delta"]?.DeepClone() });
            }
            else if (type is "response.completed" or "response.incomplete" or "response.failed" or "response.cancelled" or "response.canceled")
            {
                var response = node["response"]!.AsObject();
                if (type is not ("response.completed" or "response.incomplete")) throw new SdkException("Codex did not complete the chat response.", "codex_stream_failed");
                // Servers can provide complete calls only in the terminal snapshot, without argument deltas.
                foreach (var call in (response["output"] as JsonArray ?? new()).OfType<JsonObject>().Where(n => n["type"]?.GetValue<string>() == "function_call"))
                {
                    var key = call["id"]!.GetValue<string>();
                    if (!calls.TryGetValue(key, out var index))
                    { index = calls.Count; calls.Add(key, index); yield return Tool(index, new() { ["name"] = call["name"]?.DeepClone(), ["arguments"] = "" }, call["call_id"]?.GetValue<string>()); }
                    if (!arguments.Contains(key)) { arguments.Add(key); yield return Tool(index, new() { ["arguments"] = call["arguments"]?.DeepClone() ?? "" }); }
                }
                yield return Chunk(new(), Finish(response, calls.Count > 0));
                if (includeUsage) yield return Chunk(new(), usage: Usage(response));
                yield return new("[DONE]"); yield break;
            }
        }
    }
}
