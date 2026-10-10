# Notice!
This was designed by zombi.dev and made with GPT 6.1 Sol (max). This means it is made of a high standard by a smart AI model, and it is personally used, verified, and tested by me (a human), so issues are not expected.

*BrowserOnly is still currently TODO.*

# ChatGPT Web SDK for C#

## 1. Download the authentication extension

- **Chrome, Edge, Brave, Opera and other Chromium browsers:** [Chromium extension ZIP](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/chatgpt-web-sdk-auth-chromium-1.5.1.zip). Extract it, enable Developer mode in your browser's extension page, and choose **Load unpacked**.
- **Firefox:** [Firefox XPI](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/chatgpt-web-sdk-auth-firefox-1.5.1.xpi) or [Firefox extension ZIP](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/chatgpt-web-sdk-auth-firefox-1.5.1.zip). Open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select the XPI. This unsigned build must be loaded again after Firefox restarts.

## 2. Download the library

**v1.5.1 complete downloads include the SDK DLLs, dependencies, proxy, example app, and Chromium browser.**

| Your platform | Download |
| --- | --- |
| Windows x64 | [SDK + browser ZIP](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/ChatGPTWebSdk-Bundle-win-x64-1.5.1.zip) |
| Linux x64 | [SDK + browser tar.gz](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/ChatGPTWebSdk-Bundle-linux-x64-1.5.1.tar.gz) |
| macOS Intel | [SDK + browser tar.gz](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/ChatGPTWebSdk-Bundle-osx-x64-1.5.1.tar.gz) |
| macOS Apple Silicon | [SDK + browser tar.gz](https://github.com/zombi-dev/ChatGPTWebSDK/releases/download/v1.5.1/ChatGPTWebSdk-Bundle-osx-arm64-1.5.1.tar.gz) |

Extract the whole archive. On Linux/macOS use `tar -xzf` so executable permissions and symlinks survive. Libraries and the example need **.NET 8 or later**; the optional proxy needs **.NET 10**. Linux also needs Chromium's [system libraries](https://playwright.dev/dotnet/docs/browsers#install-system-dependencies).

[All downloads, smaller DLL-only bundles, NuGet packages, and checksums](https://github.com/zombi-dev/ChatGPTWebSDK/releases/latest).

## 3. Quick start

1. Open `https://chatgpt.com` and sign in.
2. Click **ChatGPT Web SDK Auth** in your browser toolbar. The ChatGPT tab says **Copied to clipboard!**
3. In the extracted SDK directory, run `dotnet run --project example`. Paste that one string when prompted, then type your messages.

Hybrid mode uses the bundled browser briefly for each required Sentinel handshake and closes it afterward. The conversation requests run through HTTP. Keep the copied string private.

## 4. Example code

```csharp
using OpenAI;

string authenticationString = "PASTE_THE_COPIED_STRING_HERE";
using var runtime = ChatGPTWeb.Initialize(authenticationString);

var chat = runtime.CreateClient(threadId: "my-chat")
    .GetChatClient("gpt-6");

var first = await chat.CompleteChatAsync("Remember ABC-42.");
var second = await chat.CompleteChatAsync("What did I ask you to remember?");
Console.WriteLine(second.Value.Content[0].Text);
```

For your own application, reference the replacement `ChatGPTWebSdk.OpenAI` package or supplied DLLs, and keep `browsers/` beside the application output. The included example demonstrates DLL references. Set `browser: new() { BundledBrowserDirectory = "/path/to/extracted-sdk/browsers" }` when the browser lives elsewhere.

## Models (v1.5.1)

Defaults allow **GPT-6** (`gpt-6`), **GPT-5.6 Sol** (`gpt-5-6`), and **GPT-5.5** (`gpt-5-5`). Their advertised Instant/Thinking variants are included. GPT-5.5 leaves the default set on **October 14, 2026 at 00:00 UTC**, following the date shown in ChatGPT. The official-compatible model list shows only allowed entries; `runtime.Web.Transport.GetModelsAsync(accountId)` remains raw discovery. Account access still applies.

```csharp
// Optional: attempt any backend model slug, including other/new/retired entries.
using var runtime = ChatGPTWeb.Initialize(authenticationString,
    ignoreModelRestrictions: true);
```

You can also set `IgnoreModelRestrictions = true` in `ChatGPTWebRuntimeOptions` or native `WebClientOptions`; the proxy accepts `ignoreModelRestrictions: true`. Custom `ModelAliases` are validated after resolution. Rejected models fail before generation and leave the conversation usable.

The [daily compatibility monitor](docs/MONITORING.md) checks UI/API changes and the real example app, including two-turn context and SSE. Set the repository secret `CHATGPT_WEB_MONITOR_AUTH` to enable authenticated checks. It reports confirmed changes as `[CHANGE]` and complete SDK failures as `[CRITICAL]`. Blocked checks produce workflow warnings without opening issues. See the [security policy](SECURITY.md) for private reporting and credential handling.

## MCP servers (v1.3.0)

Register remote HTTP/SSE or local stdio MCP servers, then use the same ChatClient/ResponsesClient calls and streaming methods. The SDK discovers tools, translates assistant tool requests into real MCP calls, appends the results to the linked ChatGPT thread, and returns only the final answer to your application.

```csharp
using ChatGPTWebSdk.Mcp;
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(authenticationString, mcp: new()
{
    Servers = [new() { Label = "my-tools", Endpoint = new Uri("https://YOUR_MCP_SERVER/mcp") }]
});

var chat = runtime.CreateClient(threadId: "with-tools")
    .GetChatClient("AVAILABLE_WEB_MODEL_SLUG");
var answer = await chat.CompleteChatAsync("Use the tools to answer my question.");
Console.WriteLine(answer.Value.Content[0].Text);
```

Chat and message scopes can each use independent servers, URLs and credentials, even without initialization servers:

```csharp
runtime.SetChatMcp(new()
{
    Servers = [new() { Label = "my-tools", Endpoint = new Uri("https://CHAT_MCP_SERVER/mcp") }]
}, threadId: "with-tools");

using (runtime.UseMessageMcp(new()
{
    Servers = [new() { Label = "my-tools", Endpoint = new Uri("https://MESSAGE_MCP_SERVER/mcp") }]
}))
{
    await chat.CompleteChatAsync("Use the message-specific server this time.");
}
// Later messages use the chat's server again.
```

Initialization, chat and message servers combine by label; matching labels override only within the narrower scope. Set `InheritServers = false` to replace inherited servers or use `ExcludedServers` to remove selected labels. `runtime.SetChatMcp(null, threadId: "with-tools")` clears the chat override. Native requests can set `WebTurnRequest.Mcp`. SSE and all internal tool rounds keep the selected scope.

Intermediate exchanges are hidden from SDK completion text and SSE text deltas. They remain actual turns in the remote ChatGPT thread and may be visible on the ChatGPT website. Project/temporary context and per-user bindings are retained. [MCP setup, local servers, official tool declarations, approvals and recovery](docs/MCP.md).

## Details and compatibility

A source-compatible replacement for **OpenAI .NET 2.14.0**, backed by ChatGPT's web HTTP endpoints. Initialize once, then use the original `OpenAIClient`, `ChatClient`, `ResponsesClient`, `ImageClient`, `OpenAIFileClient`, conversation clients, and their models.

The official SDK's **8,767 public/protected signatures** were compared against this assembly: **zero missing signatures**. Web equivalents implement the operations described below; unsupported platform-only operations or controls return **501**. Generation uses the ChatGPT web backend.

Live acceptance, independent remote conversation checks and test results are recorded in [docs/VERIFICATION.md](docs/VERIFICATION.md). See [authentication details](docs/AUTHENTICATION.md) for extension installation, session exports, HAR import and manual credentials.

## Initialization modes

| `ChatGPTWebMode` | Behavior |
| --- | --- |
| `ApiOnly` | HTTP requests only. Never automatically opens a browser. Supply current authorized Sentinel answers with an external session/challenge provider when required. |
| `Hybrid` **(default)** | Opens a temporary Chromium browser for each required Sentinel handshake, intercepts and aborts the draft generation request, obtains its authorized headers, closes the owned browser/context, then performs generation through HTTP. Visible by default; invisible operation is optional. |
| `BrowserOnly` | Reserved for future implementation. Initialization currently throws `NotSupportedException`. |

An optional loopback `Browser.CdpEndpoint` connects to an existing signed-in Chromium browser. The SDK creates and closes its own context/page and leaves the existing browser and original tabs open. Without CDP, it launches a temporary desktop Chromium process, connects through a nonzero loopback debugging port, and closes the owned process after the handshake. It uses the bundled Chromium when present, otherwise installed Chrome/Edge or explicitly installed Playwright Chromium. Browser.BundledBrowserDirectory selects another extracted browser directory. The page uses the actual window size and the executable's genuine user-agent and client hints. No persistent background browser is maintained. Fresh handshake answers are obtained per turn; HAR challenge tokens are never imported as reusable credentials. `Browser.UseDesktopLauncher = false` selects Playwright's standard launcher for environments that require it.

`Browser.MaxRetries` defaults to **3** and `Browser.RetryDelay` to **5 seconds**. Only failed browser startups or a repeating Cloudflare challenge are retried, after the owned resources close. A successful handshake stops retries. Account mismatches, missing protocol headers and actual generation requests are not retried. Set `MaxRetries = 0` to disable startup retries. Interactive challenges can still reject an automated browser; restarting is not a guarantee of acceptance.

### Invisible browsers and servers

```csharp
using ChatGPTWebSdk.Browser;
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(authenticationString, browser: new()
{
    Headless = true, // No window or desktop display is needed.
    Acceleration = BrowserAcceleration.Automatic // Hardware first, software fallback.
});
```

`Automatic` is the default acceleration mode. It asks Chromium to use its normal hardware path, checks Chromium's reported GPU compositing status, and restarts only its own browser with software rendering if hardware is unavailable. This happens before authentication or draft submission. `Software` selects CPU rendering immediately; `Hardware` rejects reported hardware unavailability. An unavailable GPU report is treated as unknown rather than assuming a working device. Attached CDP browsers keep their existing launch settings.

Headless Chromium and both rendering modes passed actual JavaScript and canvas checks. The live headless Sentinel probe encountered Cloudflare's interactive challenge, so `Headless` defaults to `false`. On servers, select `Headless = true` and install Chromium's system libraries; the remote service can still demand interactive sign-in or reject the headless session. An invisible session cannot display a checkbox. The SDK returns `sentinel_browser_challenge` with instructions to use a visible browser or existing signed-in CDP session. No browser stays running between turns.

Linux must also permit Chromium's normal sandbox. Ubuntu's AppArmor policy can prevent a downloaded Chromium binary from creating user namespaces; [Chromium documents a profile scoped to the browser path or an installed sandbox helper](https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md). The Linux CI job installs system dependencies and grants namespace creation only to its staged Chromium path on the ephemeral runner. The SDK keeps Chromium's sandbox enabled.

To check an extracted bundle without signing in, run `dotnet run --project example -- --verify-bundle`. It exercises the SDK's actual native launcher invisibly in automatic and software modes, verifies JavaScript and canvas rendering, then closes both browsers. Applications can run the same check through `BrowserDiagnostics.VerifyAsync`.

### Additional web operations

The supplied HAR contains **55 distinct ChatGPT service operations across 292 requests**. Every operation has a mapping in `runtime.Web.Transport.CapturedOperations`, including settings/profile reads, notifications, connector permissions and links, installed plugins/apps, billing/subscriptions, Codex tasks/usage, conversation initialization/batch reads, comparison feedback, file processing and binary asset reads. Common operations also have named methods on `runtime.Web.Transport`.

```csharp
var transport = runtime.Web.Transport;
var archived = await transport.ListConversationsAsync("default", new WebConversationQuery
{
    Archived = true, Limit = 20, Order = "updated"
});
var settings = await transport.GetUserSettingsAsync("default");
var usage = await transport.GetCodexUsageAsync("default");

// Inspect all recorded request templates and the status codes observed in the HAR.
var catalog = WebCapturedOperationsClient.Operations;
var result = await transport.CapturedOperations.SendJsonAsync("default", "ListPins",
    query: new Dictionary<string, string?> { ["item_type"] = "conversation" });
```

Add `using ChatGPTWebSdk.Web;` for the web query types and catalog. Supply your own IDs, query values and bodies; the catalog contains field names and request counts, with no captured credentials or private payloads. JSON results can be null when the backend returns an optional result, such as no default-tab recommendation. Use `SendAsync` for a binary response and dispose it after reading, or `StreamAsync` for SSE. Generation through that client still obtains fresh Sentinel authorization; `ChatGptWebClient` additionally manages scoped conversation state. These methods expose the recorded backend calls and preserve actual permission errors. A recorded 404 or canceled request does not establish a working service feature. [The operation coverage table](docs/HAR_COVERAGE.md) lists every mapping and the limits of live verification.

[Reference review](docs/REFERENCES.md) records the revisions and findings from all 13 additional repositories, alongside the original protocol and official SDK/spec sources.

## Use it in C#

Remove the official `OpenAI` package reference and replace it with the locally built `ChatGPTWebSdk.OpenAI` package. Do not reference both packages: both provide the `OpenAI` assembly and namespaces. The replacement targets **.NET 8 or newer** and is unsigned. Existing strongly named binaries must be rebuilt; copying this DLL over the official DLL is not a supported binary replacement.

```csharp
using OpenAI;
using OpenAI.Chat;

using var runtime = await ChatGPTWeb.InitializeFromHarAsync(
    @"C:\private\chatgpt.har",
    sessionDirectory: ".sessions",
    userId: "alice",
    mode: ChatGPTWebMode.Hybrid); // Default; may be omitted.

// Use an available ChatGPT web model slug, not a platform model name guessed from an API example.
ChatClient chat = new("gpt-5-6", runtime.ClientKey);
ChatCompletion first = await chat.CompleteChatAsync("Remember the reference ABC-42.");
ChatCompletion second = await chat.CompleteChatAsync("What reference did I give you?");
Console.WriteLine(second.Content[0].Text);
```

The initialization is additional setup. The subsequent clients, completion types, synchronous/asynchronous methods, and streaming methods retain their official signatures. `runtime.ClientKey` identifies an application user; it is separate from ChatGPT's access token.

For direct credential configuration and separate threads:

```csharp
using ChatGPTWebSdk.Storage;
using ChatGPTWebSdk.Web;
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(new ChatGPTWebRuntimeOptions
{
    Credentials = new StaticWebCredentialProvider("account", new WebCredentials
    {
        AccessToken = Environment.GetEnvironmentVariable("CHATGPT_WEB_ACCESS_TOKEN"),
        CookieHeader = Environment.GetEnvironmentVariable("CHATGPT_WEB_COOKIE")
    }),
    AccountId = "account",
    UserId = "alice",
    Mode = ChatGPTWebMode.Hybrid,
    SessionDirectory = ".sessions",
    // Optional: Channel = "msedge" or ExecutablePath = a locally installed browser.
    Browser = new() { Progress = Console.WriteLine }
});

var support = runtime.CreateClient(threadId: "support").GetChatClient("gpt-5-6");
#pragma warning disable OPENAI001 // Same evaluation diagnostic as the official 2.14.0 Responses surface.
var research = runtime.CreateClient(threadId: "research").GetResponsesClient();
```

`ChatGPTWebRuntimeOptions.Clients` can bind different application keys to fixed `(account, user, thread)` scopes. Set `ClientKey` to one of those keys as the default identity. Setting `ClientKey` to your existing application's key value lets existing constructor calls keep that value; it is an application identity, not a platform credential used for generation. `CreateClient(clientKey, threadId)` selects that user's client; request headers cannot override its account or user. `ModelAliases` explicitly maps API-facing names to available web model slugs.

Initialization uses an in-process virtual OpenAI endpoint; a listening proxy is optional. The default HTTP driver is **system curl**. It succeeded in authenticated live requests on the test machine, whereas .NET `HttpClient` received a Cloudflare challenge. Curl credentials are supplied through stdin, and response bodies remain streaming. `HttpDriver = ChatGPTWebHttpDriver.DotNet` selects the managed transport. Neither driver guarantees that the web service will accept generation.

## Bundled browser and optional installation

Complete downloads include Chromium matched to pinned Microsoft.Playwright 1.63.0 and its licenses. The SDK discovers `browsers/browser.json` beside the application or SDK assembly, or one directory above the proxy. `Browser.BundledBrowserDirectory` selects another bundle directory. Initialization does not download a browser.

For a source build or smaller NuGet-only installation, install Chromium once:

```powershell
dotnet build tools/ChatGPTWebSdk.Cli -c Release
pwsh tools/ChatGPTWebSdk.Cli/bin/Release/net10.0/playwright.ps1 install chromium --no-shell
```

A consuming application also receives a `playwright.ps1` script in its output directory through the Playwright dependency. Alternatively, configure an installed Chrome/Edge channel or executable. Browser installation is explicit; the library does not download browsers during initialization. `ApiOnly` needs no browser installation.

For manual browser diagnostics, run `dotnet run --project tools/ChatGPTWebSdk.Cli -- browser-check local.session.json`. It opens the SDK's browser configuration and remains open until its windows are closed. It does not submit an SDK turn. [Cloudflare documents challenge loops](https://developers.cloudflare.com/cloudflare-challenges/troubleshooting/challenge-solve-issues/) and [does not support automation frameworks for solving production challenges](https://developers.cloudflare.com/cloudflare-challenges/reference/supported-browsers/). Use a normal authorized session or an external provider if a challenge cannot complete.

For API-only generation, supply `SentinelSessionProvider`, or `RequirementsBodyProvider` plus `SentinelChallengeProvider`, using freshly authorized material for that account and turn. `WebSentinelSession` carries expiry, requirements, proof, Turnstile, observer and echo fields. `IsPrepareToken` selects the observed prepare-token header instead of the finalized-token header. Missing or expired requirements fail before an uncertain generation marker is written. Device/account restrictions remain real server errors.

## Web mappings and limits

| Surface | Implemented behavior | Current evidence |
| --- | --- | --- |
| Official C# public API | Released 2.14.0 clients, models, constructors, methods, serializers and protected surface | Binary signature comparison; zero missing signatures |
| Specification operations | All **353** operations in the pinned supplied OpenAPI snapshot, through the separate generic REST operation client | Recording-handler routing tests for every operation |
| Chat Completions | Text, binary PNG/JPEG image input, non-streaming and streaming; append-only user messages or validated complete history | Live text/image input, typed streaming/stop, restart continuation and remote graph reads |
| Responses | Text/image input, creation/streaming, latest `previous_response_id`, local retrieve/delete/input items; high reasoning effort mapped to web `extended` | Live typed streaming, completion and response retrieval; official client tests |
| Conversations API | Empty resource creation, metadata retrieval/update, item list/retrieve, deletion; first generation creates its real ChatGPT conversation | Live official creation/binding, metadata, paginated items, item retrieval and remote deletion |
| Files | Bounded upload, signed Azure PUT, processing stream, owned file list/retrieve/content; `user_data` and `vision` purposes | Live byte-for-byte round-trip; official client tests and purpose filtering |
| Images | Single-image generation and single-reference editing, returned as downloaded binary/base64 data | Live official client generation/editing and remote graph verification |
| Native web operations | Link/read/list, models/accounts, rename/archive/delete, edit/regenerate, temporary chats, assets/downloads, reconciliation | Live models, edit/regenerate, rename/archive/delete and recovery; HAR fixtures and local tests |
| Per-user state | Account/user/thread bindings, real remote IDs, atomic persistent files, exclusive locks, remote ownership, per-user response index | Isolation/concurrency/restart tests |
| Temporary chats | Web history disabled, memory-only conversation content/IDs/responses; persistent mode marker prevents accidental mode changes | Live two-turn recall with matching remote ID and no text/ID on disk; state tests |
| Project chats | New chats and continuation in an existing project, edits/regeneration, persistent binding; project sidebar/details/conversations/connector scopes/saves reads | Captured project protocol, binding tests and live evidence in the verification record |
| Sentinel | Current prepare/finalize and conduit handshake; temporary visible browser or external authorized provider | Live fresh browser handshakes and accepted HTTP generation; owned-browser cleanup |

The web adapter rejects system/developer messages, arbitrary function tools, forced structured output, temperature/top-p/token controls, background Responses, embeddings, moderation, batch jobs, fine-tuning, vector stores, platform administration, audio/video and Realtime. Exact image size/quality controls, masks, image variations, multiple requested images, file deletion and conversation item insertion/deletion also have no verified web mapping. Those official method signatures remain callable and fail explicitly through the virtual endpoint.

Remote image URLs and unsupported image detail overrides are rejected; supply binary PNG/JPEG data or an owned uploaded image ID. The Conversations API resource is persistent; create a separate thread for temporary chats. Temporary conversation content disappears when the runtime exits. Response deletion removes the local recorded response, not the underlying ChatGPT message. Conversation item results represent the branch recorded/imported by this SDK. Usage/token counts remain unknown and are never fabricated.

Native historical share, prompt-library and title methods are exposed as configurable administrative primitives; the supplied capture did not verify every one of those routes. Do not treat their availability as live acceptance evidence.

## Conversation state

Each application `(account, user, thread)` tracks the actual ChatGPT `conversation_id` and latest `parent_message_id`. A follow-up sends **only appended user messages**. ChatGPT supplies its server-side history. Local history is used for validation, resource retrieval and recovery.

A Chat Completions request containing only user messages is append mode. A request containing assistant history must match the stored prefix exactly; only its new user tail is sent. Editing and regeneration use the selected remote branch and update the local branch after confirmation. Stale response lineage is rejected rather than silently branching.

Use `runtime.Web.LinkAsync(scope, conversationId)` on a fresh thread to import an existing conversation. `EditAsync`, `RegenerateAsync` and `UpdateConversationAsync` expose web operations that have no equivalent standard API endpoint. Generation streams decode the captured `v1` channel-compressed snapshots and patches, suppress hidden analysis and secondary branches, and collect generated assets. Native streaming includes replacement markers; compatibility streaming reports an error if already-emitted text is replaced.

Interrupted or ambiguous sends leave a durable `RequiresReconciliation` marker and are not automatically retried. `ReconcileAsync` independently reads the remote graph and checks pending IDs and a finished assistant branch. If the first send loses its remote ID, `ResolveUnknownConversationAsync` requires the actual ID found in ChatGPT. Definite pre-stream HTTP rejections leave the thread safe to retry after the underlying issue is resolved.

## Projects and temporary chats

```csharp
var projectChat = runtime.CreateClient(threadId: "project-work",
    projectId: "g-p-YOUR_PROJECT_ID").GetChatClient("AVAILABLE_WEB_MODEL_SLUG");
await projectChat.CompleteChatAsync("Use this project's context.");

var temporaryChat = runtime.CreateClient(threadId: "temporary",
    temporaryChat: true).GetChatClient("AVAILABLE_WEB_MODEL_SLUG");
await temporaryChat.CompleteChatAsync("This is a temporary chat.");
```

You can also set `projectId` or `temporaryChat` on `ChatGPTWeb.Initialize`, set `ProjectId`/`TemporaryChat` in runtime options, or supply `ProjectId` on a native `WebTurnRequest`. `store=false` selects temporary mode in compatible Responses and Chat Completions requests.

Project IDs begin with `g-p-` and appear in the project's ChatGPT URL. `runtime.Web.Transport.ListProjectsAsync(accountId)` and `GetProjectAsync` expose the captured project reads. Project bindings survive process restarts and stay attached during edits, regeneration and reconciliation. Use a new thread ID to change project. Temporary content and remote IDs remain in memory and start afresh after a runtime restart.

ChatGPT does not support temporary chats inside projects; the SDK rejects that combination. The HAR observes chats in an existing project and project reads. Creating, deleting or changing a project's settings has no captured mapping.

## Optional HTTP proxy

```powershell
dotnet run --project tools/ChatGPTWebSdk.Cli -- import-har C:\private\chatgpt.har .\local.session.json
$env:CHATGPT_WEB_CONFIG = (Resolve-Path .\local.session.json).Path
dotnet run --project src/ChatGPTWebSdk.Proxy --no-launch-profile
```

The endpoint defaults to `http://127.0.0.1:5088/v1/`. Use the generated `clients[0].apiKey` from the local configuration. Each proxy key is bound to one account/application user. `X-ChatGPT-Thread-Id` selects a thread; `X-ChatGPT-User-Id` cannot change identity. The proxy uses the same adapter as the drop-in package. Compatible generation accepts `X-ChatGPT-Project-Id` and `X-ChatGPT-Temporary-Chat: true`.

The `/v1/` routes cover the mapped Responses, Chat Completions, models, files, images and Conversations operations above. Native routes are `GET /web/binding`, `POST /web/link`, `POST /web/reconcile`, `PATCH /web/conversation`, `POST /web/edit`, `POST /web/regenerate`, and `GET /capabilities`. External linking requires `allowConversationLinking=true`; account-wide transport methods are not exposed to untrusted proxy clients.

See [the example configuration](examples/proxy-config.example.json). Config `mode` values are `apiOnly`, `hybrid` and reserved `browserOnly`. `httpDriver` is `systemCurl` or `dotNet`.

## Build, package and verification

```powershell
.\build.ps1 -BundleBrowser
```

This runs the SDK and extension tests, builds the extensions, packs `ChatGPTWebSdk`, `ChatGPTWebSdk.Browser` and `ChatGPTWebSdk.OpenAI`, and publishes SDK DLLs and the proxy with their dependencies. Versioned ZIPs, NuGet packages, the Firefox XPI and checksums are collected in `artifacts/release`. The local build does not publish externally. Tooling uses .NET 10 and Node 22 or later; libraries target .NET 8.

Three workflow files run in order: **Tests → reusable Build → Release**. Branch pushes run all tests on four platforms, then call Build for the exact tested commit and its browser bundles. Pull requests run tests. Successful tested builds on the default branch publish a versioned release; the publisher executes no checked-out or downloaded code. Each update must bump the shared `VERSION` and mention it in the commit title or body. See [release instructions](docs/RELEASING.md).

```powershell
# Read-only authentication/model check through the real official client types.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json

# Creates a new two-turn test conversation only if generation is accepted.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json --send AVAILABLE_WEB_MODEL_SLUG
```

The two-turn check verifies remembered context and independently retrieves the remote message graph. The test conversation is retained for review. It passed against the live generation endpoint on the development machine, including continuation after a runtime restart. The supplied 688-entry HAR contains 11 generation streams; sanitized regression fixtures preserve their structure while removing credentials and personal text.

Public API compatibility can be reproduced with `tools/ChatGPTWebSdk.ApiCompat` using the official NuGet 2.14.0 assembly, the built replacement assembly, the replacement dependency directory, an output report path, and optionally the official dependency directory. See [the verification record](docs/VERIFICATION.md). This is a source-surface check, not a promise of binary strong-name compatibility or equivalent platform semantics.

This is an independent unofficial SDK. Keep HARs, credentials and session content local. Standard private paths are ignored by source control and omitted from packages. Further capture guidance is in [docs/HAR_GUIDE.md](docs/HAR_GUIDE.md).
