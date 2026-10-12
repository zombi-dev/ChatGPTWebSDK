using System.Text.Json.Nodes;
using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Mcp;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpEscapedFinalTests
{
    public static IEnumerable<object[]> FinalReplies()
    {
        foreach (var chunk in new[] { 1, 2, 3, 7, 17, 4096 })
        foreach (var history in new[] { "fresh", "follow-up", "mixed", "resubmitted" })
            yield return [chunk, history];
    }

    [Theory, MemberData(nameof(FinalReplies))]
    public async Task Escaped_final_markers_stay_out_of_streams_responses_and_visible_history(int chunk, string history)
    {
        using var handler = new FakeWebHandler();
        var server = new McpBridgeTests.Server();
        const string answer = "\n\nMCP_DEPLOY_OK 日本語 😀";
        var toolTurn = history == "fresh" ? 1 : 2;
        handler.StreamFactory = (body, index) => McpBridgeTests.Stream(body, index, index switch
        {
            _ when index < toolTurn => (history == "mixed" ? "" : "[[final:" + McpBridgeTests.Nonce(body) + "]]") + "Ready",
            _ when index == toolTurn => "<escape>" + McpBridgeTests.ToolReply(McpBridgeTests.Nonce(body)) + "</escape>",
            _ => " \n<escape>\n[[final:" + McpBridgeTests.Nonce(body) + "]] \t</escape>" + answer
        }, chunk);
        await using var client = new ChatGptWebClient(handler.Client().Transport, new InMemoryConversationStore(), new()
        { Servers = [new() { Label = "calculator", Client = server }] });
        var scope = new ConversationScope("account", "alice");
        string? previous = null;
        if (history != "fresh") previous = (await client.SendAsync(scope, new WebTurnRequest
        { Model = "gpt-6", Messages = [WebInputMessage.User("Hello")], UseMcp = history != "mixed" })).Response.Id;
        var request = new WebTurnRequest { Model = "gpt-6", Messages = [WebInputMessage.User("Add 2 and 3")] };
        if (history == "resubmitted") request = await new OpenAiWebAdapter(client).PrepareChatAsync(scope,
            JsonNode.Parse("""{"model":"gpt-6","messages":[{"role":"user","content":"Hello"},{"role":"assistant","content":"Ready"},{"role":"user","content":"Add 2 and 3"}]}""")!.AsObject());

        var events = new List<WebChatEvent>();
        await foreach (var item in client.StreamAsync(scope, request)) events.Add(item);

        Assert.Equal(answer, string.Concat(events.Select(e => e.Update.Delta)));
        Assert.All(events, e => Assert.StartsWith(e.Update.Text, answer, StringComparison.Ordinal));
        var response = events[^1].Response!;
        Assert.Equal(answer, response.Text);
        Assert.Equal(previous, response.PreviousResponseId);
        Assert.Equal(answer, (await client.GetResponseAsync(scope, response.Id)).Text);
        var state = await client.GetStateAsync(scope);
        Assert.Equal(answer, state.VisibleHistory![^1].Text);
        Assert.Equal(history == "fresh" ? 2 : 4, state.VisibleHistory.Count);
        Assert.False(state.RequiresReconciliation);
        Assert.True(Assert.Single(state.McpExecutions).Delivered);
        Assert.Empty(await client.GetPendingMcpToolCallsAsync(scope));
        Assert.Single(server.Calls);
        Assert.Equal(toolTurn + 1, handler.Turns.Count);
    }

    [Theory, InlineData(""), InlineData("ANSWER"), InlineData("\n\nANSWER"),
        InlineData("<escape>literal</escape>"), InlineData("[[final:probe]] quoted")]
    public void Only_the_leading_escaped_final_marker_is_removed_across_single_character_snapshots(string answer)
    {
        var decoder = new McpConversationBridge.ReplyDecoder("probe");
        var raw = "<escape>[[final:probe]]</escape>" + answer;
        var deltas = "";
        WebStreamUpdate? last = null;
        for (int length = 1; length <= raw.Length; length++)
        {
            var update = decoder.Decode(new(null, null, raw[..length], "", false, length == raw.Length, null));
            if (update is null) continue;
            Assert.StartsWith(update.Text, answer, StringComparison.Ordinal);
            Assert.False(update.IsTextReplacement);
            Assert.Null(update.Raw);
            deltas += update.Delta;
            last = update;
        }
        Assert.True(decoder.IsFinal);
        Assert.True(last!.Completed);
        Assert.Equal(answer, last.Text);
        Assert.Equal(answer, decoder.Text);
        Assert.Equal(answer, deltas);
    }

    [Theory, InlineData("<escape>[[final:probe]]literal</escape>"), InlineData("<escape>[[final:probe]]"),
        InlineData("<escape>[[final:probe]]</escape"), InlineData("<escape>[[final:other]]</escape>ANSWER")]
    public void Ordinary_or_incomplete_escaped_final_text_is_preserved(string raw)
    {
        var decoder = new McpConversationBridge.ReplyDecoder("probe");
        var deltas = "";
        for (int length = 1; length <= raw.Length; length++)
            deltas += decoder.Decode(new(null, null, raw[..length], "", false, length == raw.Length, null))?.Delta;
        Assert.Equal(raw, deltas);
        Assert.Equal(raw, decoder.Text);
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Escaped_final_markers_quoted_in_prose_or_an_explicit_final_answer_are_preserved(bool finalPrefix)
    {
        var decoder = new McpConversationBridge.ReplyDecoder("probe");
        var answer = (finalPrefix ? "" : "Example: ") + "<escape>[[final:probe]]</escape>ANSWER";
        var raw = (finalPrefix ? "[[final:probe]]" : "") + answer;
        var deltas = "";
        for (int length = 1; length <= raw.Length; length++)
            deltas += decoder.Decode(new(null, null, raw[..length], "", false, length == raw.Length, null))?.Delta;
        Assert.Equal(answer, deltas);
        Assert.Equal(answer, decoder.Text);
    }

    [Fact]
    public void Replacing_an_answer_keeps_the_escaped_final_marker_hidden()
    {
        var decoder = new McpConversationBridge.ReplyDecoder("probe");
        const string prefix = "<escape>[[final:probe]]</escape>";
        Assert.Equal("Draft", decoder.Decode(new(null, null, prefix + "Draft", "", false, false, null))!.Text);
        var update = decoder.Decode(new(null, null, prefix + "Corrected", "", true, true, null))!;
        Assert.Equal("Corrected", update.Text);
        Assert.Equal("Corrected", decoder.Text);
        Assert.Equal("", update.Delta);
        Assert.True(update.IsTextReplacement);
        Assert.True(update.Completed);
    }
}
