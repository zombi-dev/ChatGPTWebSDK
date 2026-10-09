using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;

namespace ChatGPTWebSdk.Tests;

public sealed class CompatibilityTests
{
    [Theory]
    [InlineData("temperature", "0.1")]
    [InlineData("instructions", "\"system instructions\"")]
    [InlineData("tools", "[{\"type\":\"web_search\"}]")]
    [InlineData("max_output_tokens", "100")]
    [InlineData("background", "true")]
    public async Task Unsupported_web_parameters_are_never_silently_discarded(string field, string value)
    {
        using var handler = new FakeWebHandler();
        var adapter = new OpenAiWebAdapter(handler.Client());
        var body = new JsonObject { ["model"] = "gpt-6", ["input"] = "hello", [field] = JsonNode.Parse(value) };
        await Assert.ThrowsAsync<UnsupportedWebFeatureException>(() => adapter.PrepareResponseAsync(new("account", "alice"), body));
        Assert.Empty(handler.Turns);
    }
    [Fact]
    public async Task Response_stream_has_consistent_IDs_ordered_lifecycle_and_unknown_usage()
    {
        using var handler = new FakeWebHandler();
        var adapter = new OpenAiWebAdapter(handler.Client());
        var events = new List<ServerSentEvent>();
        await foreach (var item in adapter.ResponsesAsync(new("account", "alice"), new() { ["model"] = "gpt-6", ["input"] = "hello", ["stream"] = true })) events.Add(item);
        Assert.Equal("response.created", events[0].Event);
        Assert.Equal("response.completed", events[^1].Event);
        Assert.Contains(events, item => item.Event == "response.output_item.added");
        var created = JsonNode.Parse(events[0].Data)!["response"]!;
        var completed = JsonNode.Parse(events[^1].Data)!["response"]!;
        Assert.Equal(created["id"]!.GetValue<string>(), completed["id"]!.GetValue<string>());
        Assert.Null(completed["usage"]);
        var deltas = events.Where(e => e.Event == "response.output_text.delta").Select(e => JsonNode.Parse(e.Data)!["delta"]!.GetValue<string>());
        Assert.Equal("Hello world", string.Concat(deltas));
        var stored = await adapter.Client.GetResponseAsync(new("account", "alice"), completed["id"]!.GetValue<string>());
        Assert.Equal("Hello world", stored.Text);
        Assert.Equal(Enumerable.Range(0, events.Count), events.Select(e => JsonNode.Parse(e.Data)!["sequence_number"]!.GetValue<int>()));
    }
    [Fact]
    public async Task Chat_stream_ends_with_stop_and_done_and_does_not_repeat_snapshots()
    {
        using var handler = new FakeWebHandler();
        var adapter = new OpenAiWebAdapter(handler.Client());
        var events = new List<ServerSentEvent>();
        await foreach (var item in adapter.ChatAsync(new("account", "alice"), new() { ["model"] = "gpt-6", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }), ["stream"] = true })) events.Add(item);
        Assert.Equal("[DONE]", events[^1].Data);
        Assert.Equal("stop", JsonNode.Parse(events[^2].Data)!["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal("Hello world", string.Concat(events.Take(events.Count - 1).Select(e => JsonNode.Parse(e.Data)!["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
    }
    [Fact]
    public async Task A_definite_HTTP_rejection_can_be_corrected_without_reconciliation()
    {
        using var handler = new FakeWebHandler { TurnFailure = System.Net.HttpStatusCode.Unauthorized };
        var client = handler.Client();
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(new("account", "alice"), "hello", "gpt-6"));
        Assert.False((await client.GetStateAsync(new("account", "alice"))).RequiresReconciliation);
        handler.TurnFailure = null;
        Assert.Equal("Hello world", (await client.SendAsync(new("account", "alice"), "hello", "gpt-6")).Text);
    }
}

