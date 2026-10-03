using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;

namespace ChatGPTWebSdk.Compatibility;

public static class OpenAiWebStreams
{
    public static async IAsyncEnumerable<ServerSentEvent> ResponsesAsync(this OpenAiWebAdapter adapter,
        ConversationScope scope, JsonObject body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = await adapter.PrepareResponseAsync(scope, body, ct).ConfigureAwait(false);
        bool started = false, itemStarted = false;
        string? messageId = null;
        int sequence = 0;
        ServerSentEvent Event(string type, JsonObject data)
        {
            data["type"] = type;
            data["sequence_number"] = sequence++;
            return new(data.ToJsonString(), type);
        }
        JsonObject Position() => new() { ["item_id"] = messageId, ["output_index"] = 0, ["content_index"] = 0 };
        await foreach (var webEvent in adapter.Client.StreamAsync(scope, request, ct).ConfigureAwait(false))
        {
            var update = webEvent.Update;
            if (!started)
            {
                var initial = OpenAiWebAdapter.ResponseJson(new(webEvent.ResponseId, update.ConversationId ?? "", "", request.Model, "", webEvent.CreatedAt, request.PreviousResponseId, []), "in_progress");
                initial["output"] = new JsonArray();
                if (update.ConversationId is null) initial["conversation"] = null;
                if (scope.ThreadId.StartsWith("conv_web_", StringComparison.Ordinal)) initial["conversation"] = new JsonObject { ["id"] = scope.ThreadId };
                initial.Remove("chatgpt_web");
                yield return Event("response.created", new() { ["response"] = initial });
                yield return Event("response.in_progress", new() { ["response"] = initial.DeepClone() });
                started = true;
            }
            if (update.MessageId is not null && !itemStarted)
            {
                messageId = update.MessageId;
                yield return Event("response.output_item.added", new()
                {
                    ["output_index"] = 0,
                    ["item"] = new JsonObject { ["id"] = messageId, ["type"] = "message", ["role"] = "assistant", ["status"] = "in_progress", ["content"] = new JsonArray() }
                });
                var part = Position();
                part["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() };
                yield return Event("response.content_part.added", part);
                itemStarted = true;
            }
            if (update.IsTextReplacement || messageId is not null && update.MessageId is not null && update.MessageId != messageId)
                throw new SdkException("The backend replaced already streamed text. Use non-streaming mode or the native web event stream.", "web_text_replaced");
            if (update.Delta.Length > 0)
            {
                var delta = Position();
                delta["delta"] = update.Delta;
                delta["logprobs"] = new JsonArray();
                yield return Event("response.output_text.delta", delta);
            }
            if (webEvent.Response is { } response)
            {
                var textDone = Position();
                textDone["text"] = response.Text;
                textDone["logprobs"] = new JsonArray();
                yield return Event("response.output_text.done", textDone);
                var partDone = Position();
                partDone["part"] = OpenAiWebAdapter.OutputMessage(response)["content"]![0]!.DeepClone();
                yield return Event("response.content_part.done", partDone);
                yield return Event("response.output_item.done", new() { ["output_index"] = 0, ["item"] = OpenAiWebAdapter.OutputMessage(response) });
                var completed = OpenAiWebAdapter.ResponseJson(response);
                if (scope.ThreadId.StartsWith("conv_web_", StringComparison.Ordinal)) completed["conversation"] = new JsonObject { ["id"] = scope.ThreadId };
                yield return Event("response.completed", new() { ["response"] = completed });
            }
        }
    }

    public static async IAsyncEnumerable<ServerSentEvent> ChatAsync(this OpenAiWebAdapter adapter,
        ConversationScope scope, JsonObject body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = await adapter.PrepareChatAsync(scope, body, ct).ConfigureAwait(false);
        bool roleSent = false;
        await foreach (var webEvent in adapter.Client.StreamAsync(scope, request, ct).ConfigureAwait(false))
        {
            if (webEvent.Update.IsTextReplacement) throw new SdkException("The backend replaced streamed text.", "web_text_replaced");
            JsonObject Chunk(JsonObject delta, string? finish) => new()
            {
                ["id"] = "chatcmpl_" + webEvent.ResponseId, ["object"] = "chat.completion.chunk",
                ["created"] = webEvent.CreatedAt, ["model"] = request.Model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
            };
            if (!roleSent)
            {
                yield return new(Chunk(new() { ["role"] = "assistant", ["content"] = "" }, null).ToJsonString());
                roleSent = true;
            }
            if (webEvent.Update.Delta.Length > 0) yield return new(Chunk(new() { ["content"] = webEvent.Update.Delta }, null).ToJsonString());
            if (webEvent.Response is not null)
            {
                yield return new(Chunk(new(), "stop").ToJsonString());
                yield return new("[DONE]");
            }
        }
    }
}

