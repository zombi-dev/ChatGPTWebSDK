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
