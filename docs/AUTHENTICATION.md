# Browser extension authentication

The Chromium and Firefox extensions export your signed-in ChatGPT session with one toolbar click. The ChatGPT tab displays **Copied to clipboard!** only after the clipboard write succeeds. Paste that single string into the C# initializer:

```csharp
using OpenAI;

using var runtime = ChatGPTWeb.Initialize(authenticationString);
var client = runtime.CreateClient().GetChatClient("AVAILABLE_WEB_MODEL_SLUG");
var reply = await client.CompleteChatAsync("Hello!");
```

The default is Hybrid: generation uses HTTP, and a visible temporary Chromium browser obtains a fresh authorized Sentinel handshake for each turn. The extension does not keep a background browser running. Exporting a session does not remove the need for fresh Sentinel answers.

## Install in Chromium browsers

1. Download and extract `chatgpt-web-sdk-auth-chromium-VERSION.zip` from [GitHub Releases](https://github.com/zombi-dev/ChatGPTWebSDK/releases).
2. Open your browser's extensions page, enable Developer mode, select **Load unpacked**, and choose the extracted directory containing `manifest.json`. Examples: `chrome://extensions`, `edge://extensions`, `brave://extensions`, and `vivaldi://extensions`.
3. Pin **ChatGPT Web SDK Auth** to the toolbar if necessary.
4. Open `https://chatgpt.com/`, sign in, and click the extension. The tab must remain open and focused while it copies.
5. Paste the copied `cgweb1.` string into your application's authentication setting.

The manifest requires a compatible Chromium extension API equivalent to Chrome 119 or later. Browser policies or vendor restrictions can disable unpacked extensions. The release is an unpacked extension ZIP, not a Chrome Web Store installation.

## Install in Firefox

1. Download `chatgpt-web-sdk-auth-firefox-VERSION.xpi` or extract the Firefox ZIP.
2. In Firefox 140 or later, open `about:debugging#/runtime/this-firefox` and select **Load Temporary Add-on**.
3. Choose the XPI, or the extracted `manifest.json`.
4. Open a signed-in ChatGPT tab and click **ChatGPT Web SDK Auth** in the extensions menu/toolbar.

The packaged XPI is unsigned. A temporary installation lasts until Firefox restarts. Permanent installation in normal Firefox requires Mozilla signing; this repository does not upload your extension or credentials to an add-on store. The manifest also declares Firefox for Android 142 as its minimum for the data-consent field, but live acceptance was performed on desktop Firefox. Installation and browser API restrictions on mobile vary.

Firefox builds use an event page; Chromium builds use a Manifest V3 service worker. Both use the same export code. The active tab determines the cookie store, including Firefox containers and Chromium incognito stores. If site permission is disabled, allow the extension access to ChatGPT in your browser's extension settings.

## Initialization options

```csharp
using OpenAI;
using ChatGPTWebSdk.Browser;

using var runtime = ChatGPTWeb.Initialize(
    authenticationString,
    sessionDirectory: "./sessions",
    userId: "alice",
    mode: ChatGPTWebMode.Hybrid,
    browser: new BrowserSentinelOptions { Channel = "chrome" },
    accountId: "my-chatgpt-account");
```

`accountId` and `userId` are your application's binding identifiers. They do not switch the authenticated ChatGPT account. Use a different account identifier/directory when exporting a different ChatGPT account so stored conversation bindings are not reused across accounts.

For direct HTTP-only use, import the string into the existing configurable initializer and supply your authorized Sentinel provider:

```csharp
using ChatGPTWebSdk.Web;
using OpenAI;

var credentials = WebAuthentication.Import(authenticationString);
using var runtime = ChatGPTWeb.Initialize(new ChatGPTWebRuntimeOptions
{
    Credentials = new StaticWebCredentialProvider("account", credentials),
    AccountId = "account",
    UserId = "alice",
    Mode = ChatGPTWebMode.ApiOnly,
    SentinelSessionProvider = myAuthorizedSessionProvider
});
```

HAR import and manually supplied credentials continue to work. BrowserOnly is reserved and fails explicitly at initialization.

## What the string contains

The `cgweb1.` format is Base64url-encoded UTF-8 JSON with a version, ChatGPT origin, creation time, access token, token expiry, browser user agent, selected language/client-hint/device headers, and authentication/security/context cookies. Cookie domain, path, expiry, Secure, HttpOnly, HostOnly and SameSite flags are preserved. Access-token expiry is read from the JWT as a lifetime hint; the SDK does not claim to validate that token. Expired access tokens can refresh from valid session cookies.

The extension reads `/api/auth/session` in the active ChatGPT tab and uses the browser's cookies API to include HttpOnly session cookies. It exports the observed authentication cookies, device/account context and Cloudflare/load-balancer cookies. It excludes analytics/login-state cookies and per-conversation cookies. It does not read conversations, save credentials to extension storage, send them to another service, read the clipboard, or export disposable Sentinel proofs.

The string is a credential, and the encoding is not encryption. Keep it out of source control, logs, screenshots and public issue reports. Authentication exports ending in `.auth.txt` and local `.references` files are ignored. The initializer keeps imported credentials in memory; your application chooses how to protect any saved copy. When a session is signed out or revoked, copy a new export. Clipboard errors display an error toast and never the success notification.

## Build and validate

```powershell
node --test extensions/chatgpt-auth/tests/*.test.cjs
node extensions/chatgpt-auth/build.mjs
npx --yes web-ext@10.7.0 lint --source-dir artifacts/extensions/firefox --warnings-as-errors
```

The build reads the root `VERSION` and verifies both manifest versions. It creates unpacked directories, ZIPs and an unsigned Firefox XPI in `artifacts/extensions`. The release archives contain only the explicit source scripts, manifest, generated icons and license. No local profile or credential file is included.

Primary browser references: [clipboard access](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Interact_with_the_clipboard), [browser background implementations](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/background), [Chrome cookies API](https://developer.chrome.com/docs/extensions/reference/api/cookies), and [Firefox data-consent declarations](https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/).

## Warm browser sessions (v1.6.0)

Hybrid mode reuses its isolated account context/page by default. `Browser.ReuseSession = false` restores per-handshake cleanup; `SessionIdleTimeout` and `MaxSessions` bound retained resources. `await runtime.WarmUpAsync()` loads ChatGPT ahead of a request without obtaining challenge answers. Fresh proofs are still requested for each send, and `TimingObserved` reports credential-free phase timings. Use `await using` or dispose the runtime to close owned sessions immediately. Externally supplied authentication providers remain application-owned.

`Browser.PreloadNextTurn = true` also starts the next composer navigation after capture, overlapping that work with the HTTP turn. It never submits another draft or collects another proof while idle. Set it to false to navigate only when the next handshake starts. Retaining one runtime and warming it ahead of the first request avoids repeated startup work; network challenges can still exceed one second.

For an API-key method with the native C# library alongside either ChatGPT method, see [native SDK integration](NATIVE_SDK.md). The official API uses its own authentication and endpoint and needs no Sentinel session.

Repeated Cloudflare responses leave the same browser open while waiting for the composer until `Browser.Timeout` or cancellation. An expired attempt may then use the configured bootstrap retry policy. Headless mode cannot display interactive checks. No universal one-second handshake or lower challenge rate has been verified against live ChatGPT.

## Separate Codex OAuth authentication

Version 1.6.0 also provides an opt-in stateless Codex transport, explicit auth-file import, PKCE callback exchange and serialized refresh-token rotation. Its OAuth tokens are not interchangeable with the web extension export or Sentinel answers. See [OAUTH.md](OAUTH.md). The web runtime remains the default for real ChatGPT website conversations.
