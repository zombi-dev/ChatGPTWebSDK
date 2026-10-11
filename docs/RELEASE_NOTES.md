# v1.6.0

Keep browser sessions and MCP connections ready between messages, with bounded idle cleanup and explicit ownership. Add browser-free Codex OAuth and integration with the real official C# SDK, making three explicit backend choices available. Each website generation still obtains fresh Sentinel answers; MCP recovery avoids repeating tool side effects.

## MCP reliability

- Reuse owned HTTP and stdio connections within the same account, application user and chat. Endpoint, credentials and process settings form part of the connection identity; scoped server selections and tool allowlists are checked again on each message.
- Refresh tool discovery for every message and retry transient initialization or discovery failures once by default. An expired HTTP session is replaced before execution. Authentication failures and application-supplied clients are not automatically retried.
- Resume interrupted Streamable HTTP SSE responses with GET and `Last-Event-ID` when the server supports it. Never repeat the original tool-call POST; an outcome that cannot be confirmed retains the existing reconciliation requirement.
- Accept tool blocks wrapped in Markdown fences and request one hidden correction for malformed JSON or arguments before executing any tools in that batch. Approval, call limits and durable execution records remain in effect. Progress observer failures no longer change a confirmed tool outcome.

## Browser and Sentinel

- Retain isolated account sessions for two minutes of inactivity by default, avoiding a fresh Chromium process and context on every turn. Reuse is configurable, session counts are bounded, changed credentials replace the context, and asynchronous disposal releases owned resources.
- Add `WarmUpAsync()` to load the authenticated composer ahead of a send, plus sanitized phase timings through `BrowserSentinelOptions.TimingObserved`. Warm-up does not submit a draft or request challenge answers.
- Preload the next composer after capture while the current HTTP generation runs, enabled by default. Reuse the pending navigation on the next send instead of loading it again. Preloading collects no fresh proofs; disabling it restores loading at send time.
- Keep the same browser open through repeated Cloudflare challenge pages until the configured timeout or cancellation. A repeated challenge response alone does not close or restart it; configured startup retries follow a full failed attempt.
- Keep generation interception active while a session is idle, including conversation URLs with query parameters. Fresh answers are captured only during an SDK handshake, and the browser-generated draft request is aborted before transmission.
- Preserve invisible Chromium support and automatic hardware-to-software rendering fallback. HTTP-only mode continues to accept an external authorized Sentinel provider. Actual challenge latency depends on the upstream service and sign-in state; a one-second live handshake is not guaranteed.

## Optional Codex OAuth transport

- Add a separate browser-free transport adapted from `evanzhoudev/openai-oauth`, with upstream Apache-2.0 attribution retained in NuGet packages and downloadable bundles. The default web backend continues to bind actual ChatGPT website conversations.
- Add explicit Codex auth-file import, PKCE authorization/callback exchange, account validation and serialized refresh-token rotation. Credential writes are atomic, preserve unrelated file fields and use owner-only permissions on Unix. The SDK does not automatically inspect or modify a shared Codex login.
- Support the existing official .NET Responses, Chat Completions and model clients over OAuth, including SSE, native function-call results, one-shot tool arguments and non-streaming response collection. Truncated or failed streams do not become successful completions, and generation requests are never automatically replayed.
- Support direct OAuth image generation/editing through the existing ImageClient, with base64 output, up to five reference images, bounded uploads and explicit unsupported-option errors.
- Discover account-specific models and cache their capabilities, including reasoning/verbosity defaults and Responses Lite request normalization. Keep local continuation and item-reference history isolated by account, user and thread, with count and memory limits. This stateless backend replays history and does not create website chats.

## Native official SDK and backend selection

- Add `ChatGPTWebSdk.Native`, which references the real signed OpenAI 2.14.0 NuGet library and supplies explicit web and Codex client factories. Existing native constructors retain their platform defaults; official, web and OAuth clients can coexist without globally changing transport.
- Add `ChatGPTOfficial` to both package alternatives for API-key clients with the native endpoint, authentication, retry options and platform features. Backend selection never silently retries a generation or moves a website conversation ID to another service.
- Keep the integration and drop-in packages separate to avoid duplicate OpenAI assemblies. Publish the Native NuGet alongside the existing packages and verify its three methods against the original SDK.

## Lifecycle, packaging and validation

- Add asynchronous runtime/client disposal and preserve application ownership of supplied providers and MCP clients. Update the proxy and diagnostic commands to release the resources they create.
- Require native browser bundles to pass an offline test in real Chromium: repeated challenge pages, warm-up, two fresh handshakes, one reused session and interception of generation requests. The diagnostic must load the exact packaged SDK assemblies; prepare/verify receipts include the core, browser and compatibility DLL hashes.
- Add 155 core/drop-in SDK cases, 18 native SDK integration cases and a native packaging gate, bringing the automated gate to 1,361 core/drop-in SDK, 18 native SDK, 167 automation and 12 extension tests. Packages and browser extensions share version 1.6.0; the OpenAI compatibility assembly keeps version 2.14.0.0.

See [verification details](VERIFICATION.md) for the measured local results and live-service limits. The release workflow adds all linked commits in a collapsed categorized appendix.
