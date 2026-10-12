# Use the official C# SDK alongside ChatGPT web and Codex

Install `ChatGPTWebSdk.Native` to keep the unmodified official `OpenAI` NuGet package. This integration depends on OpenAI 2.14.0 and returns its real client types for all three methods. Use it instead of `ChatGPTWebSdk.OpenAI`, the alternative drop-in replacement that supplies its own `OpenAI.dll`. Never reference both alternatives in one project.

OpenAI 2.14 requires ClientModel 1.15 for some public streaming signatures. The integration bounds that dependency below 1.16 and checks every public signature during tests. Applications requiring ClientModel 1.16 should use the drop-in package's compatibility facade or upgrade both official SDK and integration together once supported; forcing an incompatible dependency can break other native feature clients even when Chat/Responses work.

Packages are distributed as GitHub release downloads, rather than through the public NuGet registry. Download these three files from [v1.6.2](https://github.com/zombi-dev/ChatGPTWebSDK/releases/tag/v1.6.2) into one folder:

- `ChatGPTWebSdk.Native.1.6.2.nupkg`
- `ChatGPTWebSdk.1.6.2.nupkg`
- `ChatGPTWebSdk.Browser.1.6.2.nupkg`

Add that folder as a NuGet source, keeping nuget.org enabled for the official SDK and other dependencies, then install from your application's directory:

```sh
dotnet nuget add source "/absolute/path/to/downloaded-packages" --name ChatGPTWebSdk-local
dotnet add package ChatGPTWebSdk.Native --version 1.6.2
```

```csharp
using ChatGPTWebSdk.Native;
using ChatGPTWebSdk.OAuth;
using ChatGPTWebSdk.Web;
using OpenAI;

// Official API: ordinary API key, native endpoints and native SDK behavior.
OpenAIClient official = ChatGPTOfficial.CreateClient(
    Environment.GetEnvironmentVariable("OPENAI_API_KEY")!);

// ChatGPT website: authenticated HTTP with an explicit reusable Sentinel session.
await using var web = ChatGPTWeb.Initialize(new ChatGPTWebRuntimeOptions
{
    Credentials = new StaticWebCredentialProvider("default",
        WebAuthentication.Import(Environment.GetEnvironmentVariable("CHATGPT_WEB_AUTH")!)),
    UserId = "alice"
});
await web.WarmUpAsync(); // Optional; loads the composer, without requesting proofs.
OpenAIClient website = web.CreateClient(threadId: "support-chat");

// Codex: direct OAuth HTTP. The application explicitly chooses the auth file.
var session = await OAuthSession.LoadCodexAuthFileAsync(".local/codex-auth.json");
using var codex = new ChatGPTCodex(new StaticOAuthSessionProvider(session));
OpenAIClient oauth = codex.CreateClient("support-chat");

// Your application chooses one client. No automatic fallback or credential sharing.
OpenAIClient selected = official;
var answer = await selected.GetChatClient("YOUR_MODEL").CompleteChatAsync("Hello");
Console.WriteLine(answer.Value.Content[0].Text);
```

All clients can coexist in the same process. Native constructors such as `new OpenAIClient(apiKey)` always keep their official API defaults; `ChatGPTWeb.Initialize` only affects clients created through its runtime in this package. `CreateClientOptions` also supports native feature-client constructors. Supplied HTTP clients and Sentinel providers remain owned by your application.

The drop-in `ChatGPTWebSdk.OpenAI` package exposes the same `ChatGPTOfficial` helper in the `OpenAI` namespace. It explicitly bypasses global web initialization, allowing an API-key client and a web client in that process too.

| Method | Authentication | Conversation behavior | Features |
| --- | --- | --- | --- |
| Official API | API key / native SDK authentication | Official Responses/Conversations contracts | Unmodified platform API behavior, including APIs unavailable on the website |
| ChatGPT website | Extension export, HAR or supplied web credentials | Real linked ChatGPT chat, project or temporary chat | Documented web mappings and hidden-conversation MCP tools |
| Codex OAuth | Explicit OAuth session / Codex auth file | Bounded local replay, stateless upstream | Responses, Chat, tools, model discovery and image generation/editing |

Switch clients between requests. Conversation IDs and `previous_response_id` values belong to their original backend and cannot be moved between them. For a transition, explicitly supply selected messages through the new backend's supported input contract; never retry an uncertain send on another method. API keys and ChatGPT subscriptions have separate authentication and billing.

See [OAuth setup and limits](OAUTH.md), [MCP configuration](MCP.md), and the [official SDK documentation](https://github.com/openai/openai-dotnet).
