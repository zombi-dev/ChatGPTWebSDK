# MCP tools through ChatGPT conversations (v1.2.0)

Register MCP servers once, then use the normal ChatClient or ResponsesClient. The SDK lists the servers' tools, adds a tool manifest to the appended user input, recognizes assistant tool requests, calls the selected MCP server, appends an internal user message containing the results, and continues the same remote ChatGPT conversation until a final answer arrives. Every generation uses fresh Sentinel authorization through your selected authentication mode.

The application receives the final answer. Intermediate tool requests/results and protocol markers are excluded from ordinary completion text, Responses output, SSE text deltas, stored Responses input items, and the visible transcript used for Chat Completions history resubmissions. The remote thread still contains those actual turns. Result messages carry an `is_visually_hidden_from_conversation` metadata hint; the ChatGPT website's treatment of that hint is not guaranteed. This does not create a separate system/tool role in ChatGPT or erase remote messages.

The complete download's example also accepts `CHATGPT_WEB_MCP_URL`: set it to your MCP endpoint before `dotnet run --project example`. The example streams the final answer and automatically uses that server's tools.

## Quick start: a remote server

```csharp
using ChatGPTWebSdk.Mcp;
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(authenticationString, mcp: new()
{
    Servers =
    [
        new()
        {
            Label = "my-tools",
            Endpoint = new Uri("https://YOUR_MCP_SERVER/mcp"),
            Transport = McpWebTransport.StreamableHttp,
            AllowedTools = ["search", "get_document"]
            // Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer YOUR_MCP_TOKEN" }
        }
    ]
});

var chat = runtime.CreateClient(threadId: "research")
    .GetChatClient("AVAILABLE_WEB_MODEL_SLUG");

await foreach (var update in chat.CompleteChatStreamingAsync("Find and summarize the document."))
    foreach (var part in update.ContentUpdate)
        Console.Write(part.Text);
```

The SDK uses ModelContextProtocol.Core **2.2.0** for MCP initialization, JSON-RPC, paginated tool discovery, transport negotiation and typed tool-result serialization. `Auto` tries Streamable HTTP and falls back to legacy SSE; `StreamableHttp` and `Sse` select a transport explicitly. Connections created for a conversation turn close when the turn completes, fails or is disposed. The MCP server's HTTP response may itself be JSON or an SSE stream.

HTTP headers are sent only to the registered MCP origin. Redirects and legacy SSE endpoints pointing to another origin are blocked. MCP headers, endpoint URLs, local commands, environment values, and ChatGPT session credentials do not enter the tool manifest. Tool arguments and results do enter the ChatGPT conversation, as required for the model to use them.

## Servers for one chat or one message (v1.3.0)

Initialization servers remain available by default. A chat scope belongs to the exact `(account, user, thread)` binding, and a message scope covers one logical request, including every internal tool/result round. Both scopes accept the same complete `McpServerConfiguration`: their own endpoint URL, HTTP headers, transport, stdio command/arguments/environment, or application-supplied client. They do not require any servers at initialization.

```csharp
using ChatGPTWebSdk.Mcp;
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(authenticationString);
var chat = runtime.CreateClient(threadId: "research")
    .GetChatClient("AVAILABLE_WEB_MODEL_SLUG");

// Every message in this chat can use this server.
runtime.SetChatMcp(new()
{
    Servers = [new() { Label = "search", Endpoint = new Uri("https://CHAT_MCP_SERVER/mcp") }]
}, threadId: "research");

// This server is available for the next generation attempt only.
using (runtime.UseMessageMcp(new()
{
    Servers = [new() { Label = "search", Endpoint = new Uri("https://MESSAGE_MCP_SERVER/mcp") }]
}))
{
    var result = await chat.CompleteChatAsync("Use search for this request.");
    Console.WriteLine(result.Value.Content[0].Text);
}

// Uses CHAT_MCP_SERVER again, in the same linked ChatGPT conversation.
await chat.CompleteChatAsync("Search for the next document.");

// Removes the chat override and restores initialization defaults.
runtime.SetChatMcp(null, threadId: "research");
```

Selection proceeds from **initialization → chat → message**. Different labels combine. A matching label replaces the entire broader server definition within that scope; connection credentials are not merged across endpoints. Reusing the same URL in any scope is supported. Set `InheritServers = false` to replace the entire inherited set, or `ExcludedServers = ["label"]` to remove individual inherited servers. An empty replacement (`new McpScopeOptions { InheritServers = false }`) disables inherited servers for the chosen chat/message. Do not both exclude and register the same label in one scope. Duplicate labels within a scope are rejected before changing its binding.

`runtime.GetChatMcp(threadId, clientKey)` returns the chat registration; `SetChatMcp(..., clientKey: ...)` selects an application user. Native applications use `runtime.Web.SetChatMcp(conversationScope, options)` and `GetChatMcp(conversationScope)`. For a native message, pass options directly:

```csharp
var result = await runtime.Web.SendAsync(
    runtime.ResolveScope(runtime.ClientKey, "research"),
    new ChatGPTWebSdk.Web.WebTurnRequest
    {
        Model = "AVAILABLE_WEB_MODEL_SLUG",
        Messages = [ChatGPTWebSdk.Web.WebInputMessage.User("Use the local tool.")],
        Mcp = new()
        {
            InheritServers = false,
            Servers = [new() { Label = "local", Command = "node", Arguments = ["/path/to/server.mjs"] }]
        }
    });
```

`UseMessageMcp` works with unchanged ChatClient/ResponsesClient constructors and synchronous/asynchronous streaming methods. It is local to that runtime and async execution context, and **single use**: only the next Chat/Responses generation attempt consumes it, even if the `using` block contains more calls. Reads and image operations do not consume it. Await the call or enumerate the stream inside the block; disposing an unused scope cancels it. An invalid or failed generation attempt consumes the scope too, so create a new scope for a deliberate retry. Nested scopes must be disposed in reverse order; an inner scope does not consume a pending outer scope. Concurrent calls with their own scopes remain separate. Sharing one pending scope across concurrent tasks grants it to whichever generation starts first.

Chat configurations are copied when registered; message configurations are copied when selected. The selected connections remain fixed for a whole logical turn, even if the chat registration changes during tool execution. Chat registrations live in this SDK instance and are cleared on conversation deletion. They are not written to the conversation store: register them again after a restart. Connection secrets remain out of model messages and stored execution records. Application-supplied clients remain owned by the application. Temporary and project chats use the same scoping rules.

Tool limits, approval callbacks and `IsServerAllowed` remain runtime-wide policy. Each scoped server retains its own `AllowedTools` and `RequireApproval`. Approval/progress callbacks can inspect `McpToolCallContext.Server` to distinguish scoped connection configurations; this property is excluded from JSON serialization. Responses `ResponseTool.CreateMcpTool` declarations select from the effective scope and must match its current URL. They cannot create an unregistered endpoint. If a message-only tool has an uncertain outcome, explicit result recovery works after that scope expires without reconnecting or repeating the tool.

## Local stdio servers

```csharp
var mcp = new McpConversationOptions
{
    Servers =
    [
        new()
        {
            Label = "local-tools",
            Command = "node",
            Arguments = ["/absolute/path/to/your-mcp-server.mjs"],
            Transport = McpWebTransport.Stdio,
            AllowedTools = ["search"]
        }
    ]
};
using var runtime = ChatGPTWeb.Initialize(authenticationString, mcp);
```

Supply an executable and separate arguments, plus optional `WorkingDirectory` and `Environment` values. Install your server's runtime/dependencies separately. The SDK does not concatenate configuration into shell script text. Stdio servers inherit the MCP client's minimal platform environment rather than the application's complete environment. Processes launched by the SDK are owned and closed by the SDK. An application-supplied `IMcpWebServer` client is left open.

For an explicit reusable connection, use `await using var connection = await McpWebClient.ConnectAsync(serverConfiguration)`. It exposes `ListToolsAsync` and `CallToolAsync`. Register that connection as `Client` instead of `Endpoint`/`Command` if you want its session to span multiple conversation turns; the application disposes it. Exactly one connection source is allowed per configuration.

## Official Responses MCP declarations

Registered servers work automatically for normal Chat and Responses calls. You can additionally select a registered server and narrow its tools with the upstream Responses API types:

```csharp
using OpenAI.Responses;
#pragma warning disable OPENAI001

var options = new CreateResponseOptions { Model = "AVAILABLE_WEB_MODEL_SLUG" };
options.InputItems.Add(ResponseItem.CreateUserMessageItem("Search for the document."));
options.Tools.Add(ResponseTool.CreateMcpTool(
    serverLabel: "my-tools",
    serverUri: new Uri("https://YOUR_MCP_SERVER/mcp"),
    toolCallApprovalPolicy: DefaultMcpToolCallApprovalPolicy.NeverRequireApproval));

var response = await runtime.CreateClient(threadId: "research")
    .GetResponsesClient().CreateResponseAsync(options);
Console.WriteLine(response.Value.GetOutputText());
```

The label and URL must match a configured server. Per-request `allowed_tools` filters only narrow the configured allowlist, and optional request headers/authorization stay on that remote connection. Other tool types, built-in connector IDs and tunnel IDs remain unsupported. An explicit empty `tools` array disables MCP for that Responses request. Native requests can set `WebTurnRequest.UseMcp = false`. ImageClient generation/editing remains on its existing image path and does not invoke this text tool loop.

MCP output uses normal final-answer items/events, as requested for this message bridge. Hosted platform `mcp_list_tools`, `mcp_call`, and `mcp_approval_request` output items are not synthesized. To stream an options-based Responses request, set `CreateResponseOptions.StreamingEnabled = true` before calling `CreateResponseStreamingAsync(options)`.

## Tool permissions and approval

Server labels disambiguate tools with the same name. `AllowedTools` limits the tool manifest and executable calls; undeclared, unknown and disallowed tools are rejected before any call in that batch runs. The SDK validates required arguments and closed-object property names; the server remains responsible for complete JSON Schema validation.

Application-registered servers allow their selected tools by default. Set `McpServerConfiguration.RequireApproval = true` and configure `McpConversationOptions.ApproveToolCall` to approve or deny a tool based on its scope, name and arguments:

```csharp
ApproveToolCall = (call, cancellationToken) =>
    ValueTask.FromResult(call.ToolName == "search")
```

Responses declarations preserve `require_approval`: omitted/`always` requests approval; `never` skips it unless server configuration requires it; tool-name and read-only filters are supported. When approval is required and there is no callback, the tool is denied. Denial becomes a tool error result in the conversation. Callbacks are the application's approval mechanism; this adapter does not implement the platform's separate approval-response input items. `IsServerAllowed` can restrict each registered server by account/user/thread. Optional `Progress` receives executing, completed and denied events for your own UI without inserting them into completion text.

## State, streaming and recovery

The entire tool loop holds a scope-specific operation lease, so concurrent sends on the same account/user/thread cannot interleave its rounds. Tool results use the preceding assistant message as their remote parent. The final Response keeps the original application's input and previous response ID. Persistent bindings, tool execution markers and visible transcripts survive restarts. Temporary chats keep all of these in memory; their disk record contains only the existing temporary-mode marker. Project IDs are preserved on every generation turn. Deleting a conversation clears its MCP records too.

Control prefixes are buffered until classified. Tool requests are withheld; final text streams incrementally after its prefix is recognized. If the backend rewrites text already emitted, native web events report replacement; the existing OpenAI text-delta compatibility guard reports `web_text_replaced`. The loop currently applies to appended text/multimodal user turns. Automatic MCP calls during native message editing/regeneration are rejected to prevent unintended repeat tool actions; `UseMcp=false` selects the existing raw web turn path.

Defaults are **8 tool rounds**, **32 tool calls**, a **2-minute** connection/tool timeout, **256K characters** of manifest text, **64K characters** of tool arguments and **512K characters** of result text. All are configurable. Every result round requires another ChatGPT generation, so ChatGPT usage and latency include those extra turns.

A tool timeout, cancellation or connection failure can occur after the server has acted. The SDK writes an execution marker before calling the tool, records an uncertain outcome, stops, and never retries the tool automatically. Subsequent sends are blocked with `mcp_tool_outcome_unknown`. Inspect and resolve it after independently confirming the result:

```csharp
var scope = runtime.ResolveScope(runtime.ClientKey, "research");
var pending = await runtime.Web.GetPendingMcpToolCallsAsync(scope);
await runtime.Web.ResolveMcpToolCallAsync(scope, pending[0].Id, confirmedMcpResult);
// confirmedMcpResult is a JsonObject containing the independently confirmed MCP result.
// The next appended request includes that result without executing the tool again.
```

Interrupted ChatGPT generation still uses the existing `ReconcileAsync`/`ResolveUnknownConversationAsync` flow. A hidden pending user's redacted message can be confirmed by its exact node on the current remote branch; an off-branch node does not establish acceptance. Reconciliation does not approve or repeat an uncertain MCP tool call.

## Proxy configuration

In your private proxy configuration, add `mcp.servers` and grant labels explicitly to each client:

```json
{
  "mcp": {
    "servers": [
      { "label": "my-tools", "endpoint": "https://YOUR_MCP_SERVER/mcp", "transport": "streamableHttp", "allowedTools": ["search"] }
    ]
  },
  "clients": [
    { "apiKey": "YOUR_PROXY_KEY_AT_LEAST_16_CHARACTERS", "accountId": "default", "userId": "alice", "mcpServers": ["my-tools"] }
  ]
}
```

Merge this with your existing accounts/authentication configuration. A proxy client cannot register a new endpoint or gain a server permission through a request body. Clients with no granted labels receive the ordinary web conversation path. Proxy Chat/Responses streaming uses the same bridge as in-process clients. Configuration-only proxy approvals can deny calls but do not provide an interactive callback; use the in-process runtime for custom approvals.

References: [official C# MCP transports](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html), [MCP tools specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [OpenAI Responses MCP declarations](https://developers.openai.com/api/docs/guides/tools-connectors-mcp).
