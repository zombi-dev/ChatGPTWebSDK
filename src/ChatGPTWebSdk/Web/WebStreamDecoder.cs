using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

public sealed record WebStreamUpdate(string? ConversationId, string? MessageId, string Text, string Delta,
    bool IsTextReplacement, bool Completed, JsonNode? Raw);

/// <summary>Full-message SSE and JSON-pointer patch envelopes. Unknown data is retained and never interpreted as text.</summary>
public sealed class WebStreamDecoder
{
    private JsonNode? _snapshot;
    private string _lastText = "";
    private string? _lastPath;
    private string? _lastOperation;
    private string? _selectedMessage;
    private string? _finishedImageMessageId;
    private readonly Dictionary<int, JsonNode> _channels = new();
    private int _channel;
    private readonly Dictionary<string, WebAsset> _assets = new();
    public string? ConversationId { get; private set; }
    public string? MessageId { get; private set; }
    public bool Completed { get; private set; }
    public IReadOnlyList<WebAsset> Assets => _assets.Values.ToArray();

    public WebStreamUpdate Decode(ServerSentEvent item)
    {
        if (item.Data == "[DONE]")
        {
            // Current image generations can end at the finished tool node without a final text message.
            // Use that actual remote node as the next parent only after the stream confirms its end.
            MessageId ??= _finishedImageMessageId;
            Completed = true;
            return new(ConversationId, MessageId, _lastText, "", false, true, null);
        }
        if (item.Event is "delta_encoding" or "ping" or "heartbeat")
            return new(ConversationId, MessageId, _lastText, "", false, Completed, null);
        JsonNode raw;
        try { raw = JsonNode.Parse(item.Data) ?? throw new JsonException(); }
        catch (JsonException) { throw new SdkException("Invalid JSON in the ChatGPT event stream.", "invalid_web_event"); }
        if (item.Event == "error" || raw["error"] is not null)
            throw new SdkException("ChatGPT reported a generation error.", "web_generation_error");
        if (raw["c"] is JsonValue channelValue && channelValue.TryGetValue<int>(out var slot))
        { _channel = slot; _snapshot = _channels.GetValueOrDefault(slot); }
        if (raw["message"] is JsonObject) { _snapshot = raw.DeepClone(); _channels[_channel] = _snapshot; }
        else if (raw["v"] is JsonObject value && value["message"] is JsonObject && raw["o"]?.GetValue<string>() is null or "add")
        { _snapshot = value.DeepClone(); _channels[_channel] = _snapshot; _lastPath = ""; _lastOperation = "add"; }
        else if (raw["o"] is not null || raw["p"] is not null || raw["v"] is not null && _lastOperation is not null) ApplyEnvelope(raw);

        var source = _snapshot ?? raw;
        ConversationId = source["conversation_id"]?.GetValue<string>() ?? raw["conversation_id"]?.GetValue<string>() ?? ConversationId;
        var message = source["message"] as JsonObject;
        if (message is not null && message["author"]?["role"]?.GetValue<string>() is "assistant" or "tool" && message["metadata"]?["is_paragen_stream"]?.GetValue<bool>() != true && message["metadata"]?["is_visually_hidden_from_conversation"]?.GetValue<bool>() != true)
            CollectAssets(message["content"]);
        if (IsFinishedImageOutput(message)) _finishedImageMessageId = message!["id"]?.GetValue<string>();
        if (message?["author"]?["role"]?.GetValue<string>() != "assistant")
            return new(ConversationId, MessageId, _lastText, "", false, Completed, raw);
        // Tool, analysis and reasoning messages are retained in Raw but not emitted as final user-facing text.
        var channel = message["channel"]?.GetValue<string>();
        var recipient = message["recipient"]?.GetValue<string>();
        if (channel is not null and not "final" || recipient is not null and not "all" ||
            message["metadata"]?["is_visually_hidden_from_conversation"]?.GetValue<bool>() == true ||
            message["metadata"]?["is_paragen_stream"]?.GetValue<bool>() == true)
            return new(ConversationId, MessageId, _lastText, "", false, Completed, raw);
        var id = message["id"]?.GetValue<string>();
        if (_selectedMessage is not null && id != _selectedMessage)
            return new(ConversationId, MessageId, _lastText, "", false, Completed, raw);
        var content = message["content"];
        var kind = content?["content_type"]?.GetValue<string>();
        if (kind is not null and not ("text" or "multimodal_text"))
            return new(ConversationId, MessageId, _lastText, "", false, Completed, raw);
        var text = content?["parts"] is JsonArray parts
            ? string.Concat(parts.OfType<JsonValue>().Where(p => p.TryGetValue<string>(out _)).Select(p => p.GetValue<string>())) : _lastText;
        if (channel is null && text.Length == 0 && message["end_turn"]?.GetValue<bool>() != true)
            return new(ConversationId, MessageId, _lastText, "", false, Completed, raw);
        var replaced = !text.StartsWith(_lastText, StringComparison.Ordinal) || MessageId is not null && id != MessageId;
        var delta = replaced ? text : text[_lastText.Length..];
        MessageId = id ?? MessageId;
        _selectedMessage ??= id;
        _lastText = text;
        if (message["end_turn"]?.GetValue<bool>() == true && message["status"]?.GetValue<string>() == "finished_successfully") Completed = true;
        return new(ConversationId, MessageId, text, delta, replaced, Completed, raw);
    }

    private void ApplyEnvelope(JsonNode envelope)
    {
        var operation = envelope["o"]?.GetValue<string>() ?? _lastOperation;
        var path = envelope["p"]?.GetValue<string>() ?? _lastPath;
        _lastOperation = operation; _lastPath = path;
        if (operation == "patch" && envelope["v"] is JsonArray patches)
        {
            string? childOperation = null, childPath = null;
            foreach (var patch in patches)
            {
                if (patch is null) continue;
                childOperation = patch["o"]?.GetValue<string>() ?? childOperation;
                childPath = patch["p"]?.GetValue<string>() ?? childPath;
                ApplyOperation(childOperation, childPath, patch["v"]);
            }
            return;
        }
        ApplyOperation(operation, path, envelope["v"]);
    }

    private void ApplyOperation(string? operation, string? path, JsonNode? sourceValue)
    {
        if (operation is not ("append" or "replace" or "add" or "remove")) return;
        if (path is null || _snapshot is null) throw new SdkException("Patch arrived before its snapshot or path.", "unsupported_web_event");
        if (path == "")
        {
            if (operation is "add" or "replace") { _snapshot = sourceValue?.DeepClone(); if (_snapshot is not null) _channels[_channel] = _snapshot; }
            else throw new SdkException("Unsupported root patch operation.", "unsupported_web_event");
            return;
        }
        var pieces = path.Split('/').Skip(1).Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode parent = _snapshot;
        foreach (var piece in pieces[..^1])
            parent = parent is JsonArray array && int.TryParse(piece, out var index) ? array[index]! : parent[piece]!;
        var key = pieces[^1];
        var value = sourceValue?.DeepClone();
        JsonNode? old = parent is JsonArray oldArray && int.TryParse(key, out var oldIndex) && oldIndex >= 0 && oldIndex < oldArray.Count ? oldArray[oldIndex] : parent is JsonObject ? parent[key] : null;
        if (operation == "append")
        {
            if (old is JsonArray list && value is JsonArray additions) { foreach (var addition in additions) list.Add(addition?.DeepClone()); return; }
            if (old is JsonObject obj && value is JsonObject added) { foreach (var pair in added) obj[pair.Key] = pair.Value?.DeepClone(); return; }
            if (old is JsonValue scalar && scalar.TryGetValue<string>(out var text) && value is JsonValue v && v.TryGetValue<string>(out var suffix)) value = JsonValue.Create(text + suffix);
            else throw new SdkException("Unsupported append value in web patch.", "unsupported_web_event");
        }
        if (parent is JsonArray target)
        {
            if (key == "-" && operation == "add") target.Add(value);
            else if (int.TryParse(key, out var index))
            {
                if (operation == "remove") target.RemoveAt(index);
                else if (operation == "add") target.Insert(index, value);
                else target[index] = value;
            }
            else throw new SdkException("Invalid array patch index.", "unsupported_web_event");
        }
        else if (parent is JsonObject obj) { if (operation == "remove") obj.Remove(key); else obj[key] = value; }
        else throw new SdkException("Invalid patch target.", "unsupported_web_event");
    }

    private void CollectAssets(JsonNode? content)
    {
        if (content is JsonObject obj)
        {
            if (obj["content_type"]?.GetValue<string>() == "image_asset_pointer" && obj["asset_pointer"]?.GetValue<string>() is { } pointer)
            {
                var id = pointer.StartsWith("sediment://", StringComparison.Ordinal) ? pointer[11..] : pointer;
                _assets[id] = new(id, pointer, obj["mime_type"]?.GetValue<string>() ?? "image/png", obj["width"]?.GetValue<int>() ?? 0, obj["height"]?.GetValue<int>() ?? 0, obj["size_bytes"]?.GetValue<long>() ?? 0);
            }
            foreach (var pair in obj) if (pair.Value is JsonObject or JsonArray) CollectAssets(pair.Value);
        }
        else if (content is JsonArray array) foreach (var node in array) CollectAssets(node);
    }

    internal static bool IsFinishedImageOutput(JsonNode? message) =>
        message?["author"]?["role"]?.GetValue<string>() == "tool" &&
        message["status"]?.GetValue<string>() == "finished_successfully" &&
        message["metadata"]?["is_paragen_stream"]?.GetValue<bool>() != true &&
        message["metadata"]?["is_visually_hidden_from_conversation"]?.GetValue<bool>() != true &&
        ContainsImageAsset(message["content"]);

    private static bool ContainsImageAsset(JsonNode? content) => content switch
    {
        JsonObject obj => obj["content_type"]?.GetValue<string>() == "image_asset_pointer" && !string.IsNullOrEmpty(obj["asset_pointer"]?.GetValue<string>()) || obj.Any(pair => ContainsImageAsset(pair.Value)),
        JsonArray array => array.Any(ContainsImageAsset),
        _ => false
    };
}

