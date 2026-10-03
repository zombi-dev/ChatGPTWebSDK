using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class McpReconciliationTests
{
    [Theory, InlineData(true, true), InlineData(false, true), InlineData(true, false)]
    public async Task Redacted_hidden_nodes_confirm_a_pending_turn_only_when_the_exact_node_is_on_the_current_branch(bool hidden, bool onBranch)
    {
        using var handler = new FakeWebHandler(); var store = new InMemoryConversationStore(); var scope = new ConversationScope("account", "alice");
        var message = WebInputMessage.User("confirmed tool result") with { Metadata = new() { ["is_visually_hidden_from_conversation"] = hidden, ["sdk_mcp_kind"] = "tool_results" } };
        await using (var lease = await store.AcquireAsync(scope))
        {
            lease.State.ConversationId = "conversation"; lease.State.ParentMessageId = "before";
            lease.State.RequiresReconciliation = true; lease.State.PendingMessages.Add(message); await lease.SaveAsync();
        }
        handler.Remote = new JsonObject { ["current_node"] = "after", ["mapping"] = new JsonObject
        {
            ["root"] = new JsonObject { ["parent"] = null, ["message"] = null },
            ["before"] = new JsonObject { ["parent"] = "root", ["message"] = FakeWebHandler.Snapshot("conversation", "before", "tool request", true)["message"]!.DeepClone() },
            [message.Id] = new JsonObject { ["parent"] = "before", ["message"] = null },
            ["after"] = new JsonObject { ["parent"] = onBranch ? message.Id : "before", ["message"] = FakeWebHandler.Snapshot("conversation", "after", "final answer", true)["message"]!.DeepClone() }
        } };
        var client = handler.Client(store);
        if (hidden && onBranch) { await client.ReconcileAsync(scope); Assert.False((await client.GetStateAsync(scope)).RequiresReconciliation); Assert.Equal("after", (await client.GetStateAsync(scope)).ParentMessageId); }
        else { Assert.Equal("pending_turn_not_found", (await Assert.ThrowsAsync<SdkException>(() => client.ReconcileAsync(scope))).Code); Assert.True((await client.GetStateAsync(scope)).RequiresReconciliation); }
    }
}
