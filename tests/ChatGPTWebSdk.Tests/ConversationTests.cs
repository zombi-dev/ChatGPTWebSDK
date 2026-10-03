using ChatGPTWebSdk.Compatibility;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using System.Text.Json.Nodes;

namespace ChatGPTWebSdk.Tests;

public sealed class ConversationTests
{
    private static ConversationScope Scope(string user = "alice", string thread = "default", string account = "account") => new(account, user, thread);
    [Fact]
    public async Task Remote_conversation_ownership_persists_and_prevents_cross_user_links()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-" + Guid.NewGuid());
        try
        {
            var store = new FileConversationStore(directory);
            await store.ClaimRemoteConversationAsync(Scope(), "remote-conversation");
            var second = new FileConversationStore(directory);
            await second.ClaimRemoteConversationAsync(Scope(thread: "another-thread"), "remote-conversation");
            await Assert.ThrowsAsync<SdkException>(() => second.ClaimRemoteConversationAsync(Scope("bob"), "remote-conversation"));
            await second.ClaimRemoteConversationAsync(Scope("bob", account: "other-account"), "remote-conversation");
        }
        finally { TestDirectory.Delete(directory); }
    }
    [Fact]
    public async Task Second_turn_sends_only_new_input_and_latest_remote_ids()
    {
        using var handler = new FakeWebHandler();
        var client = handler.Client();
        var first = await client.SendAsync(Scope(), "First turn", "fixture-model");
        var second = await client.SendAsync(Scope(), "Second turn", "fixture-model");
        Assert.Equal("Hello world", second.Text);
        Assert.Equal(first.Response.ConversationId, second.Response.ConversationId);
        Assert.Null(handler.Turns[0].Body["conversation_id"]);
        Assert.Equal(first.Response.ConversationId, handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.Equal(first.Response.MessageId, handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        Assert.Single(handler.Turns[1].Body["messages"]!.AsArray());
        Assert.Equal("Second turn", handler.Turns[1].Body["messages"]![0]!["content"]!["parts"]![0]!.GetValue<string>());
        Assert.Equal(first.Response.Id, second.Response.PreviousResponseId);
        var stored = await client.GetResponseAsync(Scope(), first.Response.Id);
        Assert.Equal(first.Response.Id, stored.Id);
        Assert.Equal(first.Response.Text, stored.Text);
        Assert.Equal(first.Response.Input, stored.Input);
    }
    [Fact]
    public async Task Users_threads_and_accounts_have_separate_conversations_and_credentials()
    {
        using var handler = new FakeWebHandler();
        var client = handler.Client();
        foreach (var scope in new[] { Scope(), Scope("bob"), Scope(thread: "second"), Scope(account: "other-account") })
            await client.SendAsync(scope, "input", "fixture-model");
        Assert.All(handler.Turns, t => Assert.Null(t.Body["conversation_id"]));
        Assert.Equal("other-fixture-token", handler.Turns[3].Authorization);
        Assert.All(handler.Turns.Take(3), t => Assert.Equal("fixture-token", t.Authorization));
        var alice = await client.GetStateAsync(Scope());
        await Assert.ThrowsAsync<SdkException>(() => client.GetResponseAsync(Scope("bob"), alice.LastResponseId!));
    }
    [Fact]
    public async Task Same_scope_parallel_sends_are_serialized()
    {
        using var handler = new FakeWebHandler { DelayMilliseconds = 40 };
        var client = handler.Client();
        await Task.WhenAll(client.SendAsync(Scope(), "one", "fixture-model"), client.SendAsync(Scope(), "two", "fixture-model"));
        Assert.Equal("assistant-1", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
        Assert.Equal("conversation-1", handler.Turns[1].Body["conversation_id"]!.GetValue<string>());
        Assert.Equal(4, (await client.GetStateAsync(Scope())).History.Count);
    }
    [Fact]
    public async Task File_store_survives_new_client_and_store_instance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-" + Guid.NewGuid());
        try
        {
            using var handler = new FakeWebHandler();
            var first = await handler.Client(new FileConversationStore(directory)).SendAsync(Scope(), "one", "fixture-model");
            var client = handler.Client(new FileConversationStore(directory));
            await client.SendAsync(Scope(), "two", "fixture-model");
            Assert.Equal(first.Response.MessageId, handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
            Assert.Equal(4, (await client.GetStateAsync(Scope())).History.Count);
            var stored = await client.GetResponseAsync(Scope(), first.Response.Id);
            Assert.Equal(first.Response.Text, stored.Text);
            Assert.Equal(first.Response.Input, stored.Input);
        }
        finally { TestDirectory.Delete(directory); }
    }
    [Fact]
    public async Task Independent_file_stores_obey_the_same_exclusive_lock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "websdk-" + Guid.NewGuid());
        try
        {
            var first = new FileConversationStore(directory);
            var second = new FileConversationStore(directory);
            await using var lease = await first.AcquireAsync(Scope());
            using var cancellation = new CancellationTokenSource(150);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await using var blocked = await second.AcquireAsync(Scope(), cancellation.Token); });
        }
        finally { TestDirectory.Delete(directory); }
    }
    [Fact]
    public async Task Truncated_generation_is_not_retried_and_can_be_reconciled_by_message_id()
    {
        using var handler = new FakeWebHandler { StreamFactory = (body, index) => FakeWebHandler.Event(FakeWebHandler.Snapshot("conversation-1", "assistant-1", "Hel", false)) };
        var client = handler.Client();
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), "input", "fixture-model"));
        Assert.True((await client.GetStateAsync(Scope())).RequiresReconciliation);
        await Assert.ThrowsAsync<ConversationReconciliationException>(() => client.SendAsync(Scope(), "retry", "fixture-model"));
        Assert.Single(handler.Turns);
        handler.Remote = handler.CompletedRemote(handler.Turns[0].Body);
        await client.ReconcileAsync(Scope());
        handler.StreamFactory = null;
        await client.SendAsync(Scope(), "new input", "fixture-model");
        Assert.Equal("assistant-1", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Early_stream_disposal_leaves_recovery_marker()
    {
        using var handler = new FakeWebHandler();
        var client = handler.Client();
        await foreach (var item in client.StreamAsync(Scope(), new() { Model = "fixture-model", Messages = [WebInputMessage.User("input")] })) break;
        Assert.True((await client.GetStateAsync(Scope())).RequiresReconciliation);
    }
    [Fact]
    public async Task Image_without_stream_end_requires_recovery_and_reconciliation_uses_the_completed_tool_parent()
    {
        var image = new JsonObject
        {
            ["conversation_id"] = "conversation-1",
            ["message"] = new JsonObject
            {
                ["id"] = "image-tool", ["author"] = new JsonObject { ["role"] = "tool" }, ["status"] = "finished_successfully",
                ["content"] = new JsonObject { ["content_type"] = "multimodal_text", ["parts"] = new JsonArray(new JsonObject
                { ["content_type"] = "image_asset_pointer", ["asset_pointer"] = "sediment://file_fixture" }) }
            }
        };
        using var handler = new FakeWebHandler { StreamFactory = (_, _) => FakeWebHandler.Event(image) };
        var client = handler.Client();
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), "Make an image", "fixture-model"));
        Assert.True((await client.GetStateAsync(Scope())).RequiresReconciliation);
        await Assert.ThrowsAsync<ConversationReconciliationException>(() => client.SendAsync(Scope(), "follow-up", "fixture-model"));
        Assert.Single(handler.Turns);
        handler.Remote = handler.CompletedRemote(handler.Turns[0].Body);
        var mapping = handler.Remote["mapping"]!.AsObject();
        var parent = mapping["assistant-1"]!["parent"]!.GetValue<string>();
        mapping.Remove("assistant-1");
        mapping["image-tool"] = new JsonObject { ["parent"] = parent, ["message"] = image["message"]!.DeepClone() };
        handler.Remote["current_node"] = "image-tool";
        await client.ReconcileAsync(Scope());
        Assert.False((await client.GetStateAsync(Scope())).RequiresReconciliation);
        Assert.Equal("image-tool", (await client.GetStateAsync(Scope())).ParentMessageId);
        handler.StreamFactory = null;
        await client.SendAsync(Scope(), "follow-up", "fixture-model");
        Assert.Equal("image-tool", handler.Turns[1].Body["parent_message_id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Challenge_failure_does_not_mark_an_unsent_turn_uncertain()
    {
        using var handler = new FakeWebHandler { Requirements = new() { ["token"] = "fixture", ["turnstile"] = new JsonObject { ["required"] = true } } };
        var client = handler.Client();
        await Assert.ThrowsAsync<WebChallengeException>(() => client.SendAsync(Scope(), "input", "fixture-model"));
        Assert.False((await client.GetStateAsync(Scope())).RequiresReconciliation);
        Assert.Empty(handler.Turns);
    }
    [Fact]
    public async Task Previous_response_id_cannot_cross_user_or_branch_silently()
    {
        using var handler = new FakeWebHandler();
        var client = handler.Client();
        var response = await client.SendAsync(Scope(), "one", "fixture-model");
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope("bob"), new() { Model = "fixture-model", Messages = [WebInputMessage.User("input")], PreviousResponseId = response.Response.Id }));
        await client.SendAsync(Scope(), "two", "fixture-model");
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), new() { Model = "fixture-model", Messages = [WebInputMessage.User("branch")], PreviousResponseId = response.Response.Id }));
        Assert.Equal(2, handler.Turns.Count);
    }
    [Fact]
    public async Task Replayed_chat_history_is_checked_and_only_its_tail_is_sent()
    {
        using var handler = new FakeWebHandler();
        var client = handler.Client();
        var adapter = new OpenAiWebAdapter(client);
        JsonObject Request(JsonArray messages) => new() { ["model"] = "fixture-model", ["messages"] = messages };
        JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
        await client.SendAsync(Scope(), adapter.PrepareChat(Request(new(Message("user", "one")))));
        await client.SendAsync(Scope(), adapter.PrepareChat(Request(new(Message("user", "one"), Message("assistant", "Hello world"), Message("user", "two")))));
        Assert.Single(handler.Turns[1].Body["messages"]!.AsArray());
        await Assert.ThrowsAsync<SdkException>(() => client.SendAsync(Scope(), adapter.PrepareChat(Request(new(Message("user", "different"), Message("assistant", "Hello world"), Message("user", "three"))))));
    }
    [Fact]
    public async Task Link_imports_the_remote_branch_without_replaying_it()
    {
        using var handler = new FakeWebHandler();
        var input = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["id"] = "existing-user", ["content"] = new JsonObject { ["parts"] = new JsonArray(JsonValue.Create("existing input")) } }) };
        handler.Remote = handler.CompletedRemote(input);
        var client = handler.Client();
        await client.LinkAsync(Scope(), "existing-conversation");
        await client.SendAsync(Scope(), "append", "fixture-model");
        Assert.Equal("existing-conversation", handler.Turns[0].Body["conversation_id"]!.GetValue<string>());
        Assert.Equal("assistant-1", handler.Turns[0].Body["parent_message_id"]!.GetValue<string>());
        Assert.Single(handler.Turns);
    }
}

