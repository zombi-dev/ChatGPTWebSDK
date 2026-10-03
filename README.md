# ChatGPT Web SDK for C#

A source-compatible replacement for **OpenAI .NET 2.14.0**, backed by ChatGPT's web HTTP endpoints. Initialize the web runtime once, then use the original `OpenAIClient`, `ChatClient`, `ResponsesClient`, `ImageClient`, `OpenAIFileClient`, conversation clients, and their original models and signatures.

**Status: experimental, with live acceptance recorded on 2026-10-02 and 2026-10-03.** The replacement's official clients authenticated, continued text conversations across turns and runtime restarts, uploaded/downloaded an image with identical bytes, accepted binary image input, and generated/edited images. Typed Chat Completions and Responses streams, response retrieval, Conversations resources and temporary continuation also passed. Native message editing, regeneration, renaming, archiving and deletion passed live checks. Independent HTTP reads confirmed the real conversation graphs. Fresh Sentinel answers are obtained for every turn. Detailed results are recorded in [docs/VERIFICATION.md](docs/VERIFICATION.md).

The released OpenAI SDK's **8,767 public/protected signatures** were compared against this assembly: **zero missing signatures**. This preserves the C# surface; it does not create ChatGPT equivalents for platform-only services. Unsupported web operations or controls return **501**. Generation is never silently sent to the paid platform API.

## Initialization modes

| `ChatGPTWebMode` | Behavior |
| --- | --- |
| `ApiOnly` | HTTP requests only. Never automatically opens a browser. Supply current authorized Sentinel answers with an external session/challenge provider when required. |
| `Hybrid` **(default)** | Opens a visible temporary browser for each required Sentinel handshake, intercepts and aborts the draft generation request, obtains its authorized headers, closes the owned browser/context, then performs generation through HTTP. |
| `BrowserOnly` | Reserved for future implementation. Initialization currently throws `NotSupportedException`. |

An optional loopback `Browser.CdpEndpoint` connects to an existing signed-in Chromium browser. The SDK creates and closes its own context/page and leaves the existing browser and original tabs open. Without CDP, it launches a temporary desktop Chromium process, connects through a nonzero loopback debugging port, and closes the owned process after the handshake. It prefers installed Chrome/Edge and falls back to explicitly installed Playwright Chromium. The page uses the actual window size and the executable's genuine user-agent and client hints. No persistent background browser is maintained. Fresh handshake answers are obtained per turn; HAR challenge tokens are never imported as reusable credentials. `Browser.UseDesktopLauncher = false` selects Playwright's standard launcher for environments that require it.

`Browser.MaxRetries` defaults to **3** and `Browser.RetryDelay` to **5 seconds**. Only failed browser startups or a repeating Cloudflare challenge are retried, after the owned resources close. A successful handshake stops retries. Account mismatches, missing protocol headers and actual generation requests are not retried. Set `MaxRetries = 0` to disable startup retries. Interactive challenges can still reject an automated browser; restarting is not a guarantee of acceptance.

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

## Browser installation

Build the CLI, then install its Playwright Chromium runtime once:

```powershell
dotnet build tools/ChatGPTWebSdk.Cli -c Release
pwsh tools/ChatGPTWebSdk.Cli/bin/Release/net10.0/playwright.ps1 install chromium
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
| Sentinel | Current prepare/finalize and conduit handshake; temporary visible browser or external authorized provider | Live fresh browser handshakes and accepted HTTP generation; owned-browser cleanup |

The web adapter rejects system/developer messages, arbitrary function tools, forced structured output, temperature/top-p/token controls, background Responses, embeddings, moderation, batch jobs, fine-tuning, vector stores, platform administration, audio/video and Realtime. Exact image size/quality controls, masks, image variations, multiple requested images, file deletion and conversation item insertion/deletion also have no verified web mapping. Those official method signatures remain callable and fail explicitly through the virtual endpoint.

Remote image URLs and unsupported image detail overrides are rejected; supply binary PNG/JPEG data or an owned uploaded image ID. The Conversations API resource is persistent; create a separate thread for temporary chats. Temporary conversation content disappears when the runtime exits. Response deletion removes the local recorded response, not the underlying ChatGPT message. Conversation item results represent the branch recorded/imported by this SDK. Usage/token counts remain unknown and are never fabricated.

Native historical share, prompt-library and title methods are exposed as configurable administrative primitives; the supplied capture did not verify every one of those routes. Do not treat their availability as live acceptance evidence.

## Conversation state

Each application `(account, user, thread)` tracks the actual ChatGPT `conversation_id` and latest `parent_message_id`. A follow-up sends **only appended user messages**. ChatGPT supplies its server-side history. Local history is used for validation, resource retrieval and recovery.

A Chat Completions request containing only user messages is append mode. A request containing assistant history must match the stored prefix exactly; only its new user tail is sent. Editing and regeneration use the selected remote branch and update the local branch after confirmation. Stale response lineage is rejected rather than silently branching.

Use `runtime.Web.LinkAsync(scope, conversationId)` on a fresh thread to import an existing conversation. `EditAsync`, `RegenerateAsync` and `UpdateConversationAsync` expose web operations that have no equivalent standard API endpoint. Generation streams decode the captured `v1` channel-compressed snapshots and patches, suppress hidden analysis and secondary branches, and collect generated assets. Native streaming includes replacement markers; compatibility streaming reports an error if already-emitted text is replaced.

Interrupted or ambiguous sends leave a durable `RequiresReconciliation` marker and are not automatically retried. `ReconcileAsync` independently reads the remote graph and checks pending IDs and a finished assistant branch. If the first send loses its remote ID, `ResolveUnknownConversationAsync` requires the actual ID found in ChatGPT. Definite pre-stream HTTP rejections leave the thread safe to retry after the underlying issue is resolved.

## Optional HTTP proxy

```powershell
dotnet run --project tools/ChatGPTWebSdk.Cli -- import-har C:\private\chatgpt.har .\local.session.json
$env:CHATGPT_WEB_CONFIG = (Resolve-Path .\local.session.json).Path
dotnet run --project src/ChatGPTWebSdk.Proxy --no-launch-profile
```

The endpoint defaults to `http://127.0.0.1:5088/v1/`. Use the generated `clients[0].apiKey` from the local configuration. Each proxy key is bound to one account/application user. `X-ChatGPT-Thread-Id` selects a thread; `X-ChatGPT-User-Id` cannot change identity. The proxy uses the same adapter as the drop-in package.

The `/v1/` routes cover the mapped Responses, Chat Completions, models, files, images and Conversations operations above. Native routes are `GET /web/binding`, `POST /web/link`, `POST /web/reconcile`, `PATCH /web/conversation`, `POST /web/edit`, `POST /web/regenerate`, and `GET /capabilities`. External linking requires `allowConversationLinking=true`; account-wide transport methods are not exposed to untrusted proxy clients.

See [the example configuration](examples/proxy-config.example.json). Config `mode` values are `apiOnly`, `hybrid` and reserved `browserOnly`. `httpDriver` is `systemCurl` or `dotNet`.

## Build, package and verification

```powershell
.\build.ps1
```

This builds the solution, runs its tests, packs `ChatGPTWebSdk`, `ChatGPTWebSdk.Browser` and `ChatGPTWebSdk.OpenAI` into `artifacts/packages`, and publishes a local proxy build into `artifacts/proxy`. It does not upload or publish externally. Tooling uses the .NET 10 SDK; libraries target .NET 8.

```powershell
# Read-only authentication/model check through the real official client types.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json

# Creates a new two-turn test conversation only if generation is accepted.
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json --send AVAILABLE_WEB_MODEL_SLUG
```

The two-turn check verifies remembered context and independently retrieves the remote message graph. The test conversation is retained for review. It passed against the live generation endpoint on the development machine, including continuation after a runtime restart. The supplied 688-entry HAR contains 11 generation streams; sanitized regression fixtures preserve their structure while removing credentials and personal text.

Public API compatibility can be reproduced with `tools/ChatGPTWebSdk.ApiCompat` using the official NuGet 2.14.0 assembly, the built replacement assembly, a directory containing matching dependency DLLs, and an output report path. See [the verification record](docs/VERIFICATION.md). This is a source-surface check, not a promise of binary strong-name compatibility or equivalent platform semantics.

## Sources

- [OpenAI .NET release 2.14.0](https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0), commit `2e77b08828145f658ec04e49aec87abb1543c553`, MIT runtime source retained under `third_party/openai-dotnet`.
- [Supplied OpenAPI specification](https://raw.githubusercontent.com/openai/openai-openapi/refs/heads/main/openapi.yaml), pinned fingerprint/provenance in [docs/SPEC.md](docs/SPEC.md) and operations in [docs/official-operations.md](docs/official-operations.md).
- [Simatwa/WebChatGPT](https://github.com/Simatwa/WebChatGPT), historical unofficial protocol reference; its Python implementation is not vendored.
- The user's local HAR from 2026-10-02, used as protocol data, never as instructions or a request replay script.

This is an independent unofficial SDK. Keep HARs, credentials and session content local. Standard private paths are ignored by source control and omitted from packages. Further capture guidance is in [docs/HAR_GUIDE.md](docs/HAR_GUIDE.md).
