using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;
using Microsoft.Playwright;

namespace ChatGPTWebSdk.Browser;

public enum BrowserAcceleration { Automatic, Hardware, Software }

public sealed class BrowserSentinelOptions
{
    public string? CdpEndpoint { get; init; }
    public string? Channel { get; init; }
    public string? ExecutablePath { get; init; }
    public string? BundledBrowserDirectory { get; init; }
    public bool UseDesktopLauncher { get; init; } = true;
    /// <summary>Runs Chromium without a visible window. Use false when an interactive challenge needs attention.</summary>
    public bool Headless { get; init; }
    /// <summary>Automatic prefers hardware rendering and restarts only the owned browser with software rendering when hardware is unavailable.</summary>
    public BrowserAcceleration Acceleration { get; init; } = BrowserAcceleration.Automatic;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public Action<string>? Progress { get; init; }
    public string? DiagnosticScreenshotPath { get; init; }
    public string? DiagnosticComposerPath { get; init; }
    public string? DiagnosticHandshakePath { get; init; }
    public string ComposerSelector { get; init; } = "[contenteditable='true'][role='textbox'], #prompt-textarea";
    public string? SendButtonSelector { get; init; }
}

/// <summary>Opens one page for an authorized Sentinel handshake, blocks browser generation, and cleans up owned resources.</summary>
public sealed class BrowserSentinelProvider(BrowserSentinelOptions? options = null) : IWebSentinelSessionProvider
{
    private readonly BrowserSentinelOptions _options = options ?? new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(accountId, _ => new(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Captured turns each perform a fresh handshake. Expiry alone does not prove that answers may be reused.
            return await SentinelBootstrapRetry.RunAsync(_options, attemptCt => AcquireAsync(credentials, attemptCt), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<WebAuthorizedSession> AcquireAsync(WebCredentials credentials, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? browser = null; IBrowserContext? context = null; IPage? diagnosticPage = null;
        OwnedBrowser? owned = null;
        bool ownsBrowser = _options.CdpEndpoint is null;
        try
        {
            if (!ownsBrowser)
            {
                if (!Uri.TryCreate(_options.CdpEndpoint, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
                    throw new ArgumentException("The existing-browser CDP endpoint must be a loopback address.");
                browser = await playwright.Chromium.ConnectOverCDPAsync(_options.CdpEndpoint!, new() { Timeout = (float)_options.Timeout.TotalMilliseconds }).ConfigureAwait(false);
            }
            else { owned = await OwnedBrowser.LaunchAsync(playwright, _options, timeout.Token).ConfigureAwait(false); browser = owned.Browser; }
            context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block, ViewportSize = ViewportSize.NoViewport }).ConfigureAwait(false);
            if (credentials.Cookies.Count > 0)
                await context.AddCookiesAsync(credentials.Cookies.Where(c => c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow).Select(ToBrowserCookie)).ConfigureAwait(false);
            else if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
            {
                var cookies = credentials.CookieHeader.Split(';').Select(pair => pair.Trim().Split('=', 2)).Where(pair => pair.Length == 2)
                    .Select(pair => new Cookie { Name = pair[0], Value = pair[1], Url = "https://chatgpt.com/", Secure = true }).ToArray();
                await context.AddCookiesAsync(cookies).ConfigureAwait(false);
            }
            else if (!ownsBrowser && browser.Contexts.FirstOrDefault(c => c != context) is { } signedIn)
                await context.AddCookiesAsync((await signedIn.CookiesAsync(["https://chatgpt.com/"]).ConfigureAwait(false)).Select(c => new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly, SameSite = c.SameSite })).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            if (!_options.Headless) await page.BringToFrontAsync().ConfigureAwait(false);
            diagnosticPage = page;
            var captured = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Close += (_, _) => captured.TrySetException(new PlaywrightException("The temporary Sentinel page was closed before the handshake completed."));
            page.Crash += (_, _) => captured.TrySetException(new PlaywrightException("The temporary Sentinel page stopped before the handshake completed."));
            long? expires = null;
            var challengeResponses = 0;
            page.Response += async (_, response) =>
            {
                try
                {
                    if (new Uri(response.Url).Host == "chatgpt.com" && response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame
                        && response.Status == 403 && await response.HeaderValueAsync("cf-mitigated").ConfigureAwait(false) == "challenge")
                    {
                        if (_options.Headless)
                            captured.TrySetException(new SdkException("Cloudflare requires an interactive check. Set Browser.Headless=false or use an existing signed-in CDP session, then retry.", "sentinel_browser_challenge", System.Net.HttpStatusCode.ServiceUnavailable));
                        else if (Interlocked.Increment(ref challengeResponses) == 1)
                            _options.Progress?.Invoke("Cloudflare is requesting an interactive check in the temporary browser.");
                        else
                            captured.TrySetException(new SdkException("Cloudflare reloaded its challenge without accepting it. The Sentinel handshake did not reach ChatGPT.", "sentinel_browser_challenge", System.Net.HttpStatusCode.ServiceUnavailable));
                    }
                }
                catch (PlaywrightException) { /* Responses can finish after the owned page closes. */ }
                if (new Uri(response.Url).Host != "chatgpt.com" || !(response.Url.Contains("/sentinel/chat-requirements/finalize", StringComparison.Ordinal) || response.Url.Contains("/sentinel/chat-requirements/prepare", StringComparison.Ordinal)) || response.Status != 200) return;
                try { var body = JsonNode.Parse(await response.TextAsync().ConfigureAwait(false)); expires = body?["expire_at"]?.GetValue<long>(); } catch (Exception ex) when (ex is PlaywrightException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException) { }
            };
            // The web app builds its own authorized challenge headers. No conversation turn reaches the backend here.
            await page.RouteAsync("https://chatgpt.com/backend-api/**/conversation", async route =>
            {
                if (route.Request.Method != "POST") { await route.ContinueAsync().ConfigureAwait(false); return; }
                var headers = await route.Request.AllHeadersAsync().ConfigureAwait(false);
                await route.AbortAsync("aborted").ConfigureAwait(false);
                captured.TrySetResult(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            }).ConfigureAwait(false);
            await page.RouteAsync("https://chatgpt.com/backend-api/conversation", async route =>
            {
                if (route.Request.Method == "POST")
                { var headers = await route.Request.AllHeadersAsync().ConfigureAwait(false); await route.AbortAsync("aborted").ConfigureAwait(false); captured.TrySetResult(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)); }
                else await route.ContinueAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            _options.Progress?.Invoke(_options.Headless ? "Opening invisible Chromium for Sentinel." : "Opening ChatGPT for Sentinel. Complete sign-in or an interactive challenge if the browser requests it.");
            await WaitForPageAsync(page.GotoAsync("https://chatgpt.com/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = (float)_options.Timeout.TotalMilliseconds }), captured.Task, timeout.Token).ConfigureAwait(false);
            _options.Progress?.Invoke("ChatGPT page loaded. Waiting for its message composer.");
            var input = page.Locator(_options.ComposerSelector);
            await WaitForPageAsync(input.WaitForAsync(new() { Timeout = (float)_options.Timeout.TotalMilliseconds }), captured.Task, timeout.Token).ConfigureAwait(false);
            _options.Progress?.Invoke("Message composer ready. Obtaining a fresh Sentinel handshake.");
            // This unsent draft triggers the normal frontend handshake. Its generation request is intercepted and aborted.
            await input.FillAsync("Prepare SDK connection.").ConfigureAwait(false);
            if (_options.SendButtonSelector is { } send) await page.Locator(send).ClickAsync(new() { Timeout = (float)_options.Timeout.TotalMilliseconds }).ConfigureAwait(false);
            else await input.PressAsync("Enter").ConfigureAwait(false);
            if (_options.DiagnosticScreenshotPath is not null || _options.DiagnosticComposerPath is not null)
            {
                await Task.WhenAny(captured.Task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token)).ConfigureAwait(false);
                if (!captured.Task.IsCompleted) await CaptureDiagnosticsAsync(page).ConfigureAwait(false);
            }
            var headers = await captured.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_options.DiagnosticHandshakePath is { } diagnostic)
                await File.WriteAllTextAsync(diagnostic, System.Text.Json.JsonSerializer.Serialize(new { headerNames = headers.Keys.Order().ToArray(), expires })).ConfigureAwait(false);
            var isPrepareToken = false;
            if (!headers.TryGetValue("openai-sentinel-chat-requirements-token", out var token) || string.IsNullOrWhiteSpace(token))
            {
                isPrepareToken = true;
                headers.TryGetValue("openai-sentinel-chat-requirements-prepare-token", out token);
            }
            if (string.IsNullOrWhiteSpace(token))
                throw new SdkException("The browser prepared a draft without current Sentinel headers. No generation request was sent; retry the authorized handshake or use an external provider.", "sentinel_headers_missing", System.Net.HttpStatusCode.ServiceUnavailable);
            var accessToken = headers.TryGetValue("authorization", out var authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : credentials.AccessToken;
            if (Subject(credentials.AccessToken) is { } expected && Subject(accessToken) is { } actual && expected != actual)
                throw new SdkException("The Sentinel browser signed in to a different ChatGPT account.", "authentication_account_mismatch");
            var browserCookies = await context.CookiesAsync().ConfigureAwait(false);
            var exportedCookies = browserCookies.Where(c => c.Domain is "chatgpt.com" or ".chatgpt.com").Select(c => new WebCookie
            {
                Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly,
                HostOnly = !c.Domain.StartsWith('.'), ExpiresAt = c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires) : null,
                SameSite = c.SameSite switch { SameSiteAttribute.Strict => "strict", SameSiteAttribute.Lax => "lax", _ => "no_restriction" }
            }).ToArray();
            var cookieHeader = string.Join("; ", (await context.CookiesAsync(["https://chatgpt.com/"]).ConfigureAwait(false)).Select(c => c.Name + "=" + c.Value));
            var stable = new Dictionary<string, string>(credentials.Headers, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in headers)
                if (pair.Key.StartsWith("oai-", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("oai-echo-logs", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("sec-ch-", StringComparison.OrdinalIgnoreCase) || pair.Key is "chatgpt-account-id" or "accept-language" or "originator" or "x-oai-mcp-form-version" or "x-openai-web-frontend" or "x-openai-codex-window-type") stable[pair.Key] = pair.Value;
            var sentinel = new WebSentinelSession { Token = token, IsPrepareToken = isPrepareToken, ExpiresAt = expires is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow.AddMinutes(2),
                ProofToken = headers.GetValueOrDefault("openai-sentinel-proof-token"), TurnstileToken = headers.GetValueOrDefault("openai-sentinel-turnstile-token"), ObserverToken = headers.GetValueOrDefault("openai-sentinel-so-token"), EchoLogs = headers.GetValueOrDefault("oai-echo-logs") };
            var refreshed = new WebCredentials { AccessToken = accessToken, CookieHeader = cookieHeader, UserAgent = headers.GetValueOrDefault("user-agent") ?? credentials.UserAgent,
                Cookies = exportedCookies, ExpiresAt = WebAuthentication.ReadAccessTokenExpiry(accessToken) ?? credentials.ExpiresAt, Headers = stable, SentinelSession = sentinel };
            _options.Progress?.Invoke("Sentinel credentials obtained. Closing the temporary browser page.");
            return new(refreshed, sentinel);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await CaptureDiagnosticsAsync(diagnosticPage).ConfigureAwait(false);
            throw new SdkException("Timed out obtaining Sentinel credentials. Complete sign-in in the browser or configure an external provider.", "sentinel_browser_timeout", System.Net.HttpStatusCode.ServiceUnavailable);
        }
        catch (SdkException)
        {
            await CaptureDiagnosticsAsync(diagnosticPage).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            ct.ThrowIfCancellationRequested();
            await CaptureDiagnosticsAsync(diagnosticPage).ConfigureAwait(false);
            throw new SdkException("Sentinel browser bootstrap failed. Check the configured browser executable and the current ChatGPT sign-in page.", "sentinel_browser_failed", System.Net.HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            try { if (context is not null) await context.CloseAsync().ConfigureAwait(false); }
            finally { if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false); }
        }
    }

    internal static Cookie ToBrowserCookie(WebCookie cookie) => new()
    {
        Name = cookie.Name, Value = cookie.Value, Domain = cookie.Domain, Path = cookie.Path,
        Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, Expires = cookie.ExpiresAt is { } expiry ? expiry.ToUnixTimeSeconds() : -1,
        SameSite = cookie.SameSite switch { "strict" => SameSiteAttribute.Strict, "lax" => SameSiteAttribute.Lax, "no_restriction" => SameSiteAttribute.None, _ => null }
    };

    private static async Task WaitForPageAsync(Task operation, Task handshake, CancellationToken ct)
    {
        if (await Task.WhenAny(operation, handshake).WaitAsync(ct).ConfigureAwait(false) == handshake)
            await handshake.ConfigureAwait(false);
        await operation.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task CaptureDiagnosticsAsync(IPage? page)
    {
        if (page is not { IsClosed: false }) return;
        if (_options.DiagnosticComposerPath is not null)
            try
            {
                // Attribute names only: never capture input values, text, cookies, tokens or conversation content.
                var attributes = await page.EvaluateAsync<string>("() => JSON.stringify([...document.querySelectorAll('textarea, input, [contenteditable], button')].map(e => ({ tag: e.tagName, id: e.id, role: e.getAttribute('role'), editable: e.getAttribute('contenteditable'), placeholder: e.getAttribute('placeholder'), testid: e.getAttribute('data-testid'), label: e.getAttribute('aria-label') })))").ConfigureAwait(false);
                await File.WriteAllTextAsync(_options.DiagnosticComposerPath, attributes).ConfigureAwait(false);
            } catch (Exception) { /* Optional diagnostics must not mask the original failure. */ }
        if (_options.DiagnosticScreenshotPath is not null)
            try { await page.ScreenshotAsync(new() { Path = _options.DiagnosticScreenshotPath, Timeout = 5000 }).ConfigureAwait(false); } catch (Exception screenshotError) when (screenshotError is PlaywrightException or TimeoutException) { }
    }

    private static string? Subject(string? token)
    {
        try
        {
            var section = token?.Split('.')[1]; if (section is null) return null;
            section = section.Replace('-', '+').Replace('_', '/').PadRight((section.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(section))?["sub"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or System.Text.Json.JsonException) { return null; }
    }
}
