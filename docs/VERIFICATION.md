# Verification record

Earlier releases were verified on 2026-10-02 and 2026-10-03 on Windows; the v1.3.0 scope checks below were recorded on 2026-10-04 on Linux. The earlier live checks used the user's authorized ChatGPT account, fresh browser-obtained Sentinel answers, system curl, and new disposable SDK conversations. No platform API generation requests were made. Credentials, private captures and signed asset URLs are excluded from this record and from packages.

## v1.4.0 model policy, compatibility monitor and release security

Recorded on 2026-10-09 on Linux. The default model policy permits the observed GPT-6 and GPT-5.6 Sol slugs and their Instant/Thinking variants, plus GPT-5.5 before October 14, 2026 at 00:00 UTC. The website notice specifies a date; the UTC boundary is the SDK's documented interpretation. Initialization, native transport and proxy options can opt out. Rejected models fail before authentication, uploads, MCP execution or conversation-state changes. Raw discovery retains the complete model catalog for change detection.

The complete local release gate passed **1,184 SDK tests**, **82 automation tests** and **12 extension tests**: **1,278 passing cases, zero failures and zero skips**. The SDK total includes **124 new cases** beyond v1.3.0. Coverage includes the retirement boundary, aliases and opt-out paths, append/edit/regenerate behavior, model filtering, the actual automated example, sanitized reports, monitor issue escalation/recovery, release provenance and artifact validation. The solution build completed with zero warnings and zero errors.

Real-account checks initially discovered 22 model slugs. The automated .NET 8 QuickStart example passed with **gpt-6**, **gpt-5-6** and **gpt-5-5**. Each family completed an ordinary turn followed by SSE streaming that recalled a random marker in the same temporary conversation. The checks passed both in a desktop browser and with the final v1.4.0 bundle running normal Chromium on a private Xvfb virtual display, with no desktop window. The owned display and browser processes closed after the checks. No marker, response text, remote conversation ID or credentials enter monitor reports. The later virtual-display run observed 24 model slugs, including newly advertised `gpt-6-mini` and `gpt-6-t-mini`; these remain outside the default policy. The monitor correctly reported the changed model catalog and UI fingerprint as **CHANGE** while every generation check passed. The reviewed baseline was refreshed from that successful run, and comparison returned **HEALTHY**. A headless run encountered Cloudflare and correctly reported **blocked**, without claiming the SDK had stopped working. Daily Linux CI uses the tested virtual-display approach; hosted-runner acceptance still requires verification after publication.

CodeQL CLI 2.27.2 with Actions queries 0.6.37 reproduced **actions/untrusted-checkout/high** against the previous release workflow. The full Actions security-extended suite scanned all four updated workflows and reported **zero security findings**. Actionlint 1.7.12 passed. The write-permission release job has no checkout or shell execution of repository/artifact content; it checks the successful default-branch test run before downloading exact-run artifacts. Tests invoke the reusable Build workflow only after the complete test matrix succeeds. Monitor issue writes run separately from the authenticated probe.

Local v1.4.0 packaging produced the three NuGet packages, SDK DLL archive, proxy archive, Chromium/Firefox extension archives and the complete Linux Chromium bundle with checksums. The bundled Chromium **153.0.8010.12** passed invisible JavaScript/canvas checks in automatic mode with hardware-to-software fallback, and in explicit software mode. Archive inspection found no private captures, authentication exports or session files; the bundle preserves executable permissions and includes only the example's source/project files rather than its local build output. The README Notice and original heading bytes retain their previous SHA256.

A fresh .NET 8 consumer restored the new NuGet packages into an isolated cache and passed model discovery, Chat, Responses, typed SSE, linked continuation, model restrictions, runtime opt-out, extension-string initialization and HAR opt-out initialization. The upstream comparison found **8,767 official signatures, 8,844 replacement signatures and zero missing**. The replacement OpenAI assembly version remains **2.14.0.0**.

The original external SSD checkout was unavailable, so these changes and checks use a dedicated local clone. Windows and both macOS browser bundles remain checks for their CI runners. The new daily workflow requires a push to the default branch and the repository secret **CHATGPT_WEB_MONITOR_AUTH**; that secret was not configured during local verification. Expiring authentication, account limits and interactive challenges can still block hosted-runner checks. See [MONITORING.md](MONITORING.md) for setup and baseline maintenance.

## v1.3.0 scoped MCP integration

Recorded on 2026-10-04 on Linux. MCP servers can be registered at initialization, for one `(account, user, thread)` chat, or for one logical message. Scope inheritance, full replacements, label overrides, individual exclusions and empty replacements are tested in normal, project and temporary chats. No initialization servers are required. Every scope uses complete independent connection settings. A selection remains fixed through the tool loop; the next turn receives a fresh manifest after a scoped server expires.

The release gate passes **1060 .NET cases**, including **115 new scope cases**, plus **12 extension tests**, with zero skips. The new tests exercise unchanged synchronous/asynchronous ChatClient and ResponsesClient calls, typed SSE streaming, single-use and nested message lifetimes, concurrent contexts, account/user/thread isolation, caller collection mutations, approvals, permissions, deleted bindings, connection-secret exclusion and recovery of an uncertain message-only call after its registration expires.

Independent Node MCP servers authenticate separate synthetic credentials and return their actual process IDs. Official Responses tool declarations select distinct Streamable HTTP JSON/SSE and legacy SSE endpoints with the same label, return to chat defaults after a message override, and restore initialization defaults after clearing the chat. Reusing a single endpoint across scopes is tested too. Message-only stdio processes exit after the final answer. ChatGPT generation is simulated deterministically for these scope tests; this update does not claim a new real-account Sentinel/generation acceptance run. The v1.2.0 live MCP exchange evidence below remains the prior real-account verification.

The source and tests run from a matching local-disk build copy because this external drive does not support the memory-mapped reads required by .NET. Pre-existing checkout line-ending changes are preserved separately from this update. The README Notice and main-heading bytes remain unchanged in the working checkout. The shared SDK/extension version is 1.3.0; the replacement OpenAI assembly version remains 2.14.0.0. The existing Tests → Build → Release workflows retain their test-before-build gates.

Local v1.3.0 packaging completed with zero warnings/errors. The upstream comparison found **8,767 official signatures, zero missing** (8,840 replacement signatures). A fresh .NET 8 consumer restored the three new NuGet packages into an isolated cache and exercised initialization, chat, single-use message and native request scopes through real reusable stdio MCP clients and typed Responses SSE. The actual bundled Linux Chromium passed invisible JavaScript/canvas checks in automatic mode with a software fallback, and in explicit software mode. The local release directory contains the eight common assets, the Linux browser bundle and their checksums; Windows/macOS bundles remain builds for their respective CI runners.

## v1.2.0 MCP integration

The SDK now connects to explicitly registered local stdio, remote Streamable HTTP and legacy SSE MCP servers through ModelContextProtocol.Core 2.2.0. ChatClient, ResponsesClient and the proxy run the message-based tool loop within the linked conversation and filter control requests/results from application completion text. Scoped operation leases keep multi-round sends together. Allowlist, approval, size/count/timeout and per-user proxy permissions are tested. A tool whose outcome is unknown is recorded before execution, blocks subsequent sends and can be resolved without executing it again.

The local release gate passes **945 .NET cases** (147 new MCP cases), plus **12 extension tests**, with no skipped tests. New cases cover fragmented control-prefix/final-text streaming, Unicode, projects/temporary chats, visible transcript resubmissions, public response lineage after tool failure, multiple servers, approval filters, authorization boundaries, concurrent sends, cancellation/timeouts and durable recovery. Real independent Node fixture processes exercise stdio, paginated discovery, JSON/SSE Streamable HTTP and legacy SSE. Real proxy HTTP/SSE requests and the upstream `ResponseTool.CreateMcpTool` types are exercised too. Builds produce zero warnings/errors. The upstream signature comparison remains **8,767 signatures, zero missing**.

A fresh .NET 8 consumer restored the v1.2.0 NuGet packages into isolated caches and compiled the public API. Real-account checks passed with the packaged browser and fresh Sentinel handshakes:

- A reusable local stdio server returned its actual process ID through `get_pid`. ChatGPT's final answer matched that result, with control markers excluded from SDK output.
- A streamed follow-up recalled that exact result in the same conversation without another tool invocation. Independent conversation reads matched all nonhidden locally recorded message IDs. The backend omitted one hidden user result message from the returned message content; the audit explicitly accounts for that observed redaction.
- A remote local HTTP server returned the actual server process ID through a Streamable HTTP response carried as SSE. The official Responses MCP declaration selected that registered server, and typed text-delta/completed processing returned the correct final answer in a temporary chat.
- Temporary tool state and conversation IDs were absent from disk. Only the new disposable SDK test conversations were deleted, and owned server/browser processes were closed.

The first live prompt wording produced an ordinary answer without a tool call. The final protocol explicitly explains that the external client executes plain-text request blocks; subsequent live tool/result loops passed. This remains a prompt-guided message adapter, not ChatGPT's native hosted MCP integration. Intermediate exchanges remain actual remote turns; the website's visibility behavior is not guaranteed. See [MCP.md](MCP.md) for supported surfaces and limits.

The final packages include the MCP dependency and its repository license. The existing browser bundle checks pass in invisible automatic/hardware and explicit software modes. Archive inspection and the README Notice/main-heading hash check continue to exclude captures/authentication data and preserve the original top area. Publication uses the existing independent Tests, Build and Release workflow chain for the exact committed version.

## v1.1.0 additions and final package checks

The update adds project context throughout chat, Responses, image generation/editing, runtime initialization and the proxy; persistent project bindings are validated before upload or generation. Temporary chat state remains in memory, including appended and streamed turns, while only a mode marker survives a restart. Linking a remote temporary chat into persistent storage is rejected before copying its messages.

All **55 observed ChatGPT service operations** across **292 requests** have catalog mappings and request-routing/parameter-validation tests. Named methods expose additional conversation filters, settings, profiles, notifications, connectors, plugins, apps, subscriptions and Codex usage/tasks. The catalog also preserves binary and SSE response types. [HAR_COVERAGE.md](HAR_COVERAGE.md) records every operation and observed status. A valid JSON null recommendation was observed during live checks and is now accepted as an optional result.

All **29 captured read-only JSON operations with static paths and an observed 200 response** passed live through the final package, including the optional null recommendation. Dynamic project, conversation and file reads were independently exercised by the conversation/image checks below. Connector logos only returned 404 in the original capture, and the home-beacon request was canceled; neither is labeled as a successful live feature. Remaining mutation/catalog routes have routing tests and the original captured request evidence; they were not all replayed against account settings or telemetry.

The v1.1.0 test gate requires **798 passing .NET cases** (331 more than v1.0.0), plus **12 extension tests**. Builds treat warnings as errors. Three workflows run Tests, then Build, then Release; Build consumes the exact successful test SHA and publication checks that provenance. Tests cover Windows x64, Linux x64, Intel macOS and Apple Silicon macOS in CI.

A fresh .NET 8 application restored the local v1.1.0 NuGet package into an isolated cache and compiled official client calls with zero warnings/errors. The API comparison again found **8,767 upstream signatures, zero missing**. The Windows complete bundle was extracted from its ZIP, and its Chromium was used with the global Playwright browser cache deliberately unavailable. All of the following real-account checks passed:

- Four captured project read endpoints, two-turn project marker recall after a runtime restart, and independent confirmation of the remote project's gizmo ID.
- Project message editing, regeneration, deletion, image generation, upload of the generated reference image, image editing and binary download through the official ImageClient.
- Two-turn temporary marker recall, streamed continuation, absence of temporary messages/remote IDs on disk, and local binding cleanup when the backend's persistent-history delete endpoint already returns 404.
- Nine fresh Sentinel handshakes, automatic packaged-browser discovery and removal of all browser profiles created by those checks. Only the newly created SDK test conversations were deleted.

The actual SDK native launcher and bundled Chromium **153.0.8010.12** passed JavaScript and canvas rendering in invisible automatic and explicit software modes. Chromium reported hardware acceleration for the former and software rendering for the latter. A separate visible software-rendered Sentinel handshake also succeeded. Both live invisible Sentinel probes returned Cloudflare's interactive challenge; invisible mode therefore remains optional and the visible default is retained. Headless engine support does not establish unattended acceptance by ChatGPT. Every platform Build job exercises both launcher/rendering modes before uploading its browser bundle.

Archive inspection found no HARs, session files, user profiles, private HAR paths or known authentication values in the nine locally built Windows/common archives. The Firefox package passed web-ext 10.7.0 with zero errors, notices or warnings. README's original Notice area and main heading retain their original SHA256. All 13 additional references were cloned/reviewed; revisions, licenses and findings are in [REFERENCES.md](REFERENCES.md).

## Released SDK compatibility

The replacement vendors the runtime source of [OpenAI .NET 2.14.0](https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0), commit `2e77b08828145f658ec04e49aec87abb1543c553`. Its dependency on `System.ClientModel` matches the released package's `1.15.0` dependency.

The comparison tool enumerates exported types and declared public/protected constructors, methods, accessors and fields. It compares canonical type names, parameter names and optional flags against the released NuGet assembly. The baseline has **8,767 signatures; none are missing** from the replacement.

Only pipeline initialization and the web Realtime rejection guard alter upstream runtime source. The original types and serializers are retained. This check establishes the compared C# surface, not strong-name binary compatibility, platform feature equivalence or compatibility with later releases. Rebuild consumers after replacing the package.

Reproduce the comparison after building:

```powershell
dotnet run --project tools/ChatGPTWebSdk.ApiCompat -- `
  C:\baseline\OpenAI.dll `
  src/ChatGPTWebSdk.OpenAI/bin/Release/net8.0/OpenAI.dll `
  tests/ChatGPTWebSdk.Tests/bin/Release/net10.0 `
  api-compat-report.json
```

Use the `net8.0/OpenAI.dll` from the official NuGet package **2.14.0** as the baseline. The dependency directory must contain matching `System.ClientModel`, configuration abstractions and other dependencies. The comparison tool uses an isolated assembly load context for each assembly.

## Live acceptance

These checks exercise the actual released public client types compiled into the replacement. Independent graph verification fetches the real conversation over HTTP and checks that every locally recorded message ID exists remotely.

| Check | Result and independent evidence |
| --- | --- |
| Authentication and models | `OpenAIModelClient.GetModelsAsync` authenticated and deserialized available web model slugs. |
| Text continuation | `ChatClient` sent two appended turns; the second recalled a random marker from the first. Independent HTTP reads confirmed both turns in the same remote graph. |
| Restart continuation | A new process/runtime loaded the existing binding, recalled the first marker, and confirmed all recorded IDs remotely. |
| File upload/download | `OpenAIFileClient` uploaded a valid 64×64 PNG, completed processing, then downloaded byte-for-byte identical data. |
| Image input | `ChatClient` accepted a binary PNG content part and returned text; independent HTTP reads confirmed its input and response IDs. |
| Image generation | `ImageClient.GenerateImageAsync` returned downloaded image bytes. The artifact was visually inspected and the remote graph independently verified. |
| Image editing | `ImageClient.GenerateImageEditAsync` uploaded the reference image and returned downloaded output bytes, with independent remote graph verification. |
| Interrupted image recovery | The completed remote image-tool node was independently read and reconciled without resending the generation request. |
| Native conversation management | Edit and regeneration produced new remote nodes; rename, archive/unarchive and deletion succeeded. The binding was cleared and a separate .NET 8 consumer independently confirmed the deleted test conversation returned HTTP 404. |
| Temporary continuation | Two appended temporary turns recalled a random marker using the same remote ID. The persistent file contained neither the marker nor the remote ID. |
| Responses streaming | Official typed text-delta and completed events arrived; retrieving the recorded response returned the same text. Independent HTTP reads confirmed the real conversation graph. |
| Chat Completions streaming | Official typed content updates and `Stop` finish reason arrived; the real conversation graph was independently verified. |
| Conversations resource | Official create/bind, metadata retrieve/update, paginated items and single-item retrieval passed. The first Response created a real ChatGPT conversation, whose graph was verified before deleting the disposable resource and remote conversation. |
| Installable package consumer | A separate .NET 8 console application restored the three local NuGet packages and their transitive dependencies, compiled original SDK constructors and authenticated with `OpenAIModelClient`. The Playwright installation script was copied into its output. |

Retained acceptance conversations for the account owner:

- [Two appended SDK turns](https://chatgpt.com/c/6ac00f2d-6614-83ed-a830-294fc915029c).
- [Restart continuation](https://chatgpt.com/c/6ac00c24-0cd0-83ed-8cff-4768d385c8be).
- [Binary image input](https://chatgpt.com/c/6ac0119e-8cc0-83ed-8af6-4bb18bb8f19d).
- [Image generation](https://chatgpt.com/c/6ac014c9-717c-83eb-b53c-ad93bd9e8880).
- [Image editing](https://chatgpt.com/c/6ac0151c-dfe4-83eb-989a-9071926476fb).
- [Responses streaming](https://chatgpt.com/c/6ac0beb6-9330-83eb-9ab3-f9d785d16455).
- [Chat Completions streaming](https://chatgpt.com/c/6ac0bec0-3c34-83eb-b2fb-f7933c125e56).

These links require the account owner's sign-in. They are not public shares.

## Protocol and local coverage

The v1.0.0 solution tests passed **467 tests, zero failures and zero skips**, plus **12 extension tests**. Builds completed with **zero warnings and zero errors**. Packages were inspected as ZIP archives: no HARs, session files, cookies or private capture paths were included. Versioned packages, SDK DLLs, proxy bundles, browser extensions and checksums are built into `artifacts/release`. Packages are attached to GitHub Releases; no NuGet registry publication is configured.

## v1.0.0 authentication and release verification

- The actual Chromium and Firefox Manifest V3 extensions were loaded in owned desktop browser profiles. Their action handler read the active ChatGPT session and the browser cookies API, copied the export, and created the in-page notification. The resulting Windows clipboard strings were saved only in ignored private test files. This exercised the real extension handler; the toolbar click itself was invoked through the extension's debugging context.
- Both actual exports included a current access token and scoped HttpOnly session cookies. The export excludes analytics and per-conversation cookies. The Firefox package passed Mozilla's web-ext 10.7.0 validator with zero errors, notices or warnings.
- A separate .NET 8 application restored the v1.0.0 packages into a fresh isolated NuGet cache. `ChatGPTWeb.Initialize(authenticationString)` imported each browser's export, compiled original OpenAI client constructors, and authenticated the official model client.
- The packaged initializer in Hybrid mode obtained two fresh Sentinel handshakes, generated two appended turns and recalled a random marker in the second turn. Both owned Sentinel browser instances closed after their handshakes.
- The final replacement retains all 8,767 official public/protected signatures. The GitHub workflow passed actionlint 1.7.12 locally. Its hosted build/release result is available from the repository's [Actions page](https://github.com/zombi-dev/ChatGPTWebSDK/actions).

The Firefox release XPI is unsigned. Temporary desktop installation was verified through Firefox's add-on debugging mechanism; permanent installation requires Mozilla signing. Other Chromium vendors, Firefox containers/incognito cookie stores, and Android support are covered by code/manifest checks where applicable, not separate live account tests.

- Every one of the **353** operations in the pinned supplied OpenAPI snapshot has an HTTP routing test. This exercises the separate generic REST operation client, not 353 successful ChatGPT web equivalents.
- The supplied HAR contained **688 entries and 11 generation streams**. Sanitized fixtures retain stream structure while removing account credentials and private text. Every captured stream decodes the expected primary message, text length and generated asset count.
- Official client tests cover synchronous/asynchronous chat, Chat Completions and Responses streaming, recorded response retrieval, previous-response indexing, scoped Conversations metadata/items, file transfer, and image generation/editing.
- State tests cover atomic persistence, exclusive locks across store instances, account/user/thread isolation, response-index restart, branch edits, regeneration, pending-turn reconciliation and temporary content remaining in memory.
- Proxy tests exercise the same adapter through an actual local Kestrel TCP listener, authentication, user isolation, streaming/error envelopes and unsupported operations.
- Sentinel tests assert a fresh provider invocation and distinct answer headers on each appended turn. Missing challenges fail before marking an unsent turn uncertain. Browser errors preserve their HTTP status through both streaming and non-streaming official clients.
- Browser startup retries stop after success, preserve the final error, honor cancellation during the delay, reject invalid retry limits, and exclude account mismatches, missing protocol headers and upstream generation errors. Defaults are three retries with a five-second delay.
- Image protocol tests cover final assistant text and completed image-tool-only results. A truncated image stream requires reconciliation and cannot silently retry.

## Findings incorporated into the implementation

Reusing a Sentinel answer on an appended turn caused a live 403 even before its nominal expiry. The SDK now obtains a fresh answer for each generation turn and keeps only refreshed authentication credentials between turns.

The live ChatGPT composer no longer consistently had `#prompt-textarea`. The browser provider now also recognizes a contenteditable textbox and supports configurable selectors.

New live browser requests on 2026-10-03 also used `openai-sentinel-chat-requirements-prepare-token` with fresh proof/Turnstile answers, while the original capture used finalized requirements tokens. The provider and HTTP transport preserve the observed header form. An intercepted draft lacking either supported form fails promptly; closing the temporary page also ends the handshake promptly.

Later browser startup checks encountered a Cloudflare challenge that reloaded with HTTP 403 after a checkbox interaction. The supplied console log establishes a challenge loop, but does not establish which browser signal caused rejection. The SDK detects repeated Cloudflare challenge navigation responses and performs at most three startup retries, five seconds apart, without retrying submitted conversation turns. [Cloudflare documents automation framework limitations](https://developers.cloudflare.com/cloudflare-challenges/reference/supported-browsers/).

The default launcher now starts a desktop browser process with an owned temporary profile and a nonzero loopback debugging port, then attaches for Sentinel. It uses the real window size and executable's actual user-agent/client hints. A direct diagnostic reported `navigator.webdriver = false` and a 929×925 viewport in a 945×1020 window. Five consecutive fresh handshakes completed without a challenge loop during the final temporary/streaming/Conversations bundle. All four remaining live acceptance cases passed. The cleanup audit found transient locks in Chrome's metrics/cache files after browser exit. Cleanup now waits up to ten seconds for those locks, with a locked-file regression test. A subsequent live streaming check passed after that change, and its owned profile was removed; the only remaining owned profile belonged to the deliberately open manual diagnostic. Generic third-party detector results are not an acceptance guarantee.

Current image streams can finish with a successful tool image node and `[DONE]`, without a final assistant text message. The decoder uses that actual remote node as the next parent. An interrupted stream still requires an independent read before continuation.

A malformed initial test PNG caused a genuine upstream rejection. It was replaced with a valid PNG; upload, byte identity and binary image input subsequently passed. Unsupported image controls are validated before uploading an image.

## Reproduction and limits

```powershell
.\build.ps1
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-sdk .\local.session.json --send AVAILABLE_WEB_MODEL_SLUG
dotnet run --project tools/ChatGPTWebSdk.Cli -- resume-sdk .\local.session.json SAVED_SDK_SMOKE_THREAD AVAILABLE_WEB_MODEL_SLUG
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-feature .\local.session.json AVAILABLE_WEB_MODEL_SLUG image
dotnet run --project tools/ChatGPTWebSdk.Cli -- smoke-features .\local.session.json AVAILABLE_WEB_MODEL_SLUG temporary,responses-stream,chat-stream,conversations
dotnet run --project tools/ChatGPTWebSdk.Cli -- browser-check .\local.session.json
```

Feature checks create new test conversations. Image checks save output bytes beside the private configuration. The edit/regenerate and Conversations checks delete only their own disposable test conversation after verification. `inspect-sdk` exports a chosen SDK test thread's remote snapshot to an explicitly selected private local path; its optional `--reconcile` performs recovery without resending.

HTTP-only mode has fixture coverage using an external authorized provider; no live browser-free Sentinel provider was supplied in this session. Existing-browser CDP attachment has implementation coverage but was not exercised because no CDP session was available. Live checks exercised the default temporary-browser mode. Browser-only mode is intentionally reserved and rejects initialization.

Platform-only functions remain explicit 501 responses through the virtual web adapter. Unverified historical share/prompt-library/title primitives, voice/audio/video, PDF document input, arbitrary function tools and exact image controls have no live acceptance claim here. Server authentication, account limits, interactive challenges and future web protocol changes can affect acceptance. The successful tests establish this account/machine/protocol snapshot.
