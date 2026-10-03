# Verification record

Recorded on 2026-10-02 and 2026-10-03 on the development Windows machine. Live checks used the user's authorized ChatGPT account, fresh browser-obtained Sentinel answers, system curl, and new disposable SDK conversations. No platform API generation requests were made. Credentials, private captures and signed asset URLs are excluded from this record and from packages.

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

The current solution tests passed **443 tests, zero failures and zero skips**. Builds completed with **zero warnings and zero errors**. Packages were inspected as ZIP archives: no HARs, session files, cookies or private capture paths were included. A local proxy build was published into `artifacts/proxy`; packages were not uploaded to a registry.

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
