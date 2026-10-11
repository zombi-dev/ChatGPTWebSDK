# Optional Codex OAuth transport

Version 1.6.0 includes a direct HTTP transport adapted from [evanzhoudev/openai-oauth](https://github.com/EvanZhouDev/openai-oauth). It uses `https://chatgpt.com/backend-api/codex/` with a Codex OAuth access token and the account ID. It does not obtain Sentinel answers or start Chromium. Authentication and account entitlements still apply.

Choose this explicitly when you want stateless Codex Responses. Keep `ChatGPTWeb.Initialize(...)` for real ChatGPT website conversations, projects, temporary chats, uploads and other existing web operations. The SDK never switches an uncertain web generation to OAuth automatically. OAuth model slugs and access are discovered separately from the web model allowlist.

## Existing OAuth credentials

Select an auth file explicitly. The SDK does not search `CODEX_HOME`, read the OS keyring, or import the running Codex application's credentials automatically. A web extension export and a Platform API key are different credential types.

```csharp
using ChatGPTWebSdk.OAuth;
using OpenAI;

var session = await OAuthSession.LoadCodexAuthFileAsync("/private/codex.auth.json");
using var runtime = new ChatGPTCodex(new StaticOAuthSessionProvider(session));
var client = runtime.CreateClient(threadId: "research");
var models = await client.GetOpenAIModelClient().GetModelsAsync();
var model = models.Value.First().Id;

var responses = client.GetResponsesClient();
var first = await responses.CreateResponseAsync(model, "Hello");
Console.WriteLine(first.Value.GetOutputText());
await foreach (var update in responses.CreateResponseStreamingAsync(model, "Explain SSE"))
    if (update is OpenAI.Responses.StreamingResponseOutputTextDeltaUpdate delta)
        Console.Write(delta.Delta);

var chat = client.GetChatClient(model);
Console.WriteLine((await chat.CompleteChatAsync("Hello")).Value.Content[0].Text);
```

The explicit transport also exposes `CodexOAuthHttpHandler` for an ordinary `HttpClient`. An injected upstream client remains application-owned. Without one, the handler creates an HTTP client with redirects disabled and disposes it with the runtime. Keep redirects disabled on supplied clients to avoid forwarding credentials to another origin. No fake client identity or browser fingerprint is introduced.

## Refresh and persistence

`StaticOAuthSessionProvider` uses the supplied token until you replace it. `RefreshingOAuthSessionProvider` refreshes within five minutes of expiry, serializes concurrent refreshes, preserves omitted refresh/id tokens, validates account identity, and invokes an optional persistence callback before returning rotated credentials. A persistence failure is retried on the next request without exchanging the already rotated token again.

```csharp
var authPath = "/private/my-app.auth.json";
var session = await OAuthSession.LoadCodexAuthFileAsync(authPath);
using var authHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
var authorization = new OpenAIOAuthClient(authHttp);
var provider = new RefreshingOAuthSessionProvider(session, authorization,
    (rotated, ct) => rotated.SaveCodexAuthFileAsync(authPath, ct));
using var runtime = new OpenAI.ChatGPTCodex(provider);
```

`SaveCodexAuthFileAsync` writes only the requested path, preserves unrelated JSON fields, replaces the file atomically and uses mode `0600` on Unix. Windows uses the destination directory's inherited ACLs. Plaintext auth files must remain private and outside source control. Coordinate refresh ownership if multiple applications share a session; concurrent independent refreshes can invalidate each other's tokens. Prefer an application-owned session and storage callback over unattended modification of another application's login. Neither generation nor token HTTP requests have an automatic retry loop.

## PKCE sign-in helpers

`OpenAIOAuthClient.CreateLoginRequest()` creates a cryptographically random state, verifier and S256 challenge. Its default registered callback is `http://localhost:1455/auth/callback`. The application opens `AuthorizationUri` in the user's normal browser and supplies its own loopback callback listener or another registered redirect handler. These helpers do not start a browser or listener implicitly.

```csharp
var login = authorization.CreateLoginRequest();
// Your application opens login.AuthorizationUri and receives the complete callback URI.
OAuthSession authenticated = await authorization.ExchangeCallbackAsync(login, callbackUri);
await authenticated.SaveCodexAuthFileAsync(authPath);
```

The callback must match the original address and state and contain a single authorization code. Wrong hosts/paths, repeated parameters, errors and fragments are rejected before token exchange. `OpenAIOAuthOptions` allows an explicit OAuth client ID, issuer and scope. A custom redirect must be registered for that client. Exceptions and `ToString()` representations omit tokens, authorization codes and provider error bodies. PKCE/state verification does not verify a JWT signature; the upstream service authenticates the bearer token.

## Requests, tools and models

Responses accepts text or structured input, including supported image and tool items. It forces `store=false` and streaming upstream, supplies `instructions` and includes encrypted reasoning content for local continuation. Non-streaming requests collect a terminal response from SSE. `max_output_tokens` is removed because this Codex contract does not implement the Platform output-budget parameter. Do not depend on that parameter as a limit with this transport.

Chat Completions translates user/developer/assistant messages, text and image parts, modern function tools, tool choices, tool results, reasoning effort and structured text output into Responses. Tool declarations and argument deltas are translated back for the original typed .NET clients. Complete arguments arriving only in an output item or terminal snapshot are emitted once. Function tools are returned to the application for execution; the web runtime's hidden MCP loop remains available on the web backend. Responses-native tools are passed through subject to upstream support.

Model discovery uses the account's `/models?client_version=...` catalog, excludes hidden/non-API models and caches capabilities for five minutes. The pinned default client version is `0.144.1`, matching the reviewed reference; set `CodexOAuthOptions.ClientVersion` explicitly when needed. Reasoning/verbosity defaults fill missing settings. Models marked `use_responses_lite` receive the corresponding header, developer tool/instruction inputs and all-turn reasoning context. Discovery failure does not fabricate a model catalog; generation can still ask the upstream endpoint to decide model access. Set `DiscoverModelCapabilities=false` when you supply a known-compatible request directly.

The implemented routes are model list/retrieve, Responses create, Chat Completions create, image generation and image editing, including synchronous/asynchronous and streaming use through the original clients. Website conversation resources, stored response retrieval, background jobs and other Platform routes return an explicit unsupported-operation error on this transport. Existing web features are unchanged.

## Images

The original `ImageClient` can generate and edit images through the Codex OAuth image endpoints. For example, use `client.GetImageClient("gpt-image-2")` and the usual `GenerateImageAsync` or `GenerateImageEditAsync` methods. Select `GeneratedImageFormat.Bytes` when providing response options. The output is base64 JSON, and generated image bytes are returned through the existing .NET types.

Editing converts multipart image inputs into data URLs and accepts one to five PNG/JPEG/WebP/GIF references, with a 50 MiB per-image and 64 MiB total upload limit. Prompt, model, count, background, quality and size are normalized for the upstream contract. Image streaming, URL response format, masks, input fidelity, moderation, compression, output-format and partial-image controls return explicit errors before upload. Generated images are not automatically saved in a ChatGPT website conversation. These routes have fixture and original-client coverage; real OAuth image generation has not been verified in this update.

## Local history and lifetime

Codex requests are stateless and replay complete input history. With `CacheResponseHistory=true`, `previous_response_id` and `item_reference` expand from completed responses in the same account, application user and thread. Unknown, evicted, cross-scope and cross-runtime IDs fail before generation. Create one runtime per application user or set `CodexOAuthOptions.UserId`, and use `CreateClient(threadId)` for separate histories. This cache is in memory and creates no website conversation ID.

`MaxCachedResponses=256` bounds response/item entries. `MaxContextCharacters=8 MiB` bounds each request and retained response; total retained input/output characters are bounded to four times that value. Entries are evicted in insertion order. Set `CacheResponseHistory=false` and replay complete history yourself when persistence or other control is needed. Dispose the runtime to release owned HTTP resources and clear cached history.

SSE cancellation closes the upstream response, including when a chat stream is disposed after its initial role update. Only terminal events confirm a complete response. EOF or `[DONE]` after a partial snapshot is an error; no generation POST is repeated. Local fixtures verify these contracts and original .NET client compatibility. New live OAuth sign-in, token refresh and generation have not been exercised against a real account in this update.
