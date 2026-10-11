using System.Diagnostics;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Resources;
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
    /// <summary>Reuse an isolated account context. Each send still obtains a fresh handshake.</summary>
    public bool ReuseSession { get; init; } = true;
    /// <summary>Reload the composer after capture while the API turn runs. Never requests challenge answers while idle.</summary>
    public bool PreloadNextTurn { get; init; } = true;
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxSessions { get; init; } = 2;
    /// <summary>Sanitized phase measurements; contains no URLs, account IDs or credentials.</summary>
    public Action<BrowserSentinelTiming>? TimingObserved { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public Action<string>? Progress { get; init; }
    /// <summary>Optional SHA-256 observation of UI selectors and public asset URLs; never receives page content or credentials.</summary>
    public Action<string>? UiFingerprintObserved { get; init; }
    public string? DiagnosticScreenshotPath { get; init; }
    public string? DiagnosticComposerPath { get; init; }
    public string? DiagnosticHandshakePath { get; init; }
    public string ComposerSelector { get; init; } = "[contenteditable='true'][role='textbox'], #prompt-textarea";
    public string? SendButtonSelector { get; init; }
}

public sealed record BrowserSentinelTiming(string Stage, TimeSpan Elapsed, bool ReusedSession);

/// <summary>Obtains fresh authorization in an isolated browser session, with bounded idle reuse and explicit disposal.</summary>
public sealed class BrowserSentinelProvider : IWebSentinelSessionProvider, IAsyncDisposable
{
    private readonly BrowserSentinelOptions _options;
    private readonly AsyncLeasePool<BrowserSentinelWorkspace> _sessions;
    private readonly Func<WebCredentials, CancellationToken, Task<BrowserSentinelWorkspace>> _createWorkspace;
    public BrowserSentinelProvider(BrowserSentinelOptions? options = null) : this(options ?? new(), null) { }
    internal BrowserSentinelProvider(BrowserSentinelOptions options, Func<WebCredentials, CancellationToken, Task<BrowserSentinelWorkspace>>? createWorkspace)
    {
        _options = options;
        _sessions = new(_options.ReuseSession, _options.SessionIdleTimeout, _options.MaxSessions);
        _createWorkspace = createWorkspace ?? ((credentials, ct) => BrowserSentinelWorkspace.CreateAsync(_options, credentials, ct));
    }
    public ValueTask DisposeAsync() => _sessions.DisposeAsync();

    public async ValueTask<WebAuthorizedSession> GetSessionAsync(string accountId, WebCredentials credentials, JsonObject turnBody, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(credentials);
        return await SentinelBootstrapRetry.RunAsync(_options, async attemptCt => await AcquireAsync(accountId, credentials, false, attemptCt).ConfigureAwait(false) ?? throw new InvalidOperationException("The handshake returned no session."), ct).ConfigureAwait(false);
    }

    /// <summary>Load the authenticated ChatGPT page ahead of a send. Does not request or cache challenge answers.</summary>
    public async Task WarmUpAsync(string accountId, WebCredentials credentials, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(credentials);
        await AcquireAsync(accountId, credentials, true, ct).ConfigureAwait(false);
    }

    private async Task<WebAuthorizedSession?> AcquireAsync(string accountId, WebCredentials credentials, bool warmUp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        var total = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();
        AsyncLeasePool<BrowserSentinelWorkspace>.Lease? lease = null;
        IBrowserContext? context = null;
        IPage? diagnosticPage = null;
        EventHandler<IResponse>? responseHandler = null;
        bool success = false, created = false;
        var challengeResponses = 0;
        try
        {
            Task<BrowserSentinelWorkspace> Create(CancellationToken token)
            { created = true; return _createWorkspace(credentials, token); }
            lease = await _sessions.AcquireAsync(accountId, Create, timeout.Token).ConfigureAwait(false);
            if (!lease.Resource.Matches(credentials))
            {
                lease.Invalidate(); await lease.DisposeAsync().ConfigureAwait(false);
                lease = await _sessions.AcquireAsync(accountId, Create, timeout.Token).ConfigureAwait(false);
            }
            var workspace = lease.Resource;
            context = workspace.Context;
            var page = diagnosticPage = workspace.Page;
            Observe("browser_ready", phase.Elapsed, !created); phase.Restart();
            if (!_options.Headless) await page.BringToFrontAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            var captured = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.Capture = captured;
            long? expires = null;
            page.Response += responseHandler = async (_, response) =>
            {
                try
                {
                    if (new Uri(response.Url).Host == "chatgpt.com" && response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame
                        && response.Status == 403 && await response.HeaderValueAsync("cf-mitigated").ConfigureAwait(false) == "challenge")
                    {
                        if (Interlocked.Increment(ref challengeResponses) == 1)
                            ReportProgress(_options.Headless
                                ? "Cloudflare is requesting an interactive check. Waiting for the configured timeout; use a visible session if interaction is required."
                                : "Cloudflare is requesting an interactive check. Keeping the same browser open while waiting for it to clear.");
                    }
                }
                catch (PlaywrightException) { /* Responses can finish after the owned page closes. */ }
                if (new Uri(response.Url).Host != "chatgpt.com" || !(response.Url.Contains("/sentinel/chat-requirements/finalize", StringComparison.Ordinal) || response.Url.Contains("/sentinel/chat-requirements/prepare", StringComparison.Ordinal)) || response.Status != 200) return;
                try { var body = JsonNode.Parse(await response.TextAsync().ConfigureAwait(false)); expires = body?["expire_at"]?.GetValue<long>(); } catch (Exception ex) when (ex is PlaywrightException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException) { }
            };
            ReportProgress(_options.Headless ? "Opening invisible Chromium for Sentinel." : "Opening ChatGPT for Sentinel. Complete sign-in or an interactive challenge if the browser requests it.");
            if (workspace.NeedsNavigation) await WaitForPageAsync(workspace.NavigateAsync(_options.Timeout), captured.Task, timeout.Token).ConfigureAwait(false);
            ReportProgress("ChatGPT page loaded. Waiting for its message composer.");
            var input = page.Locator(_options.ComposerSelector);
            await WaitForPageAsync(input.WaitForAsync(new() { Timeout = (float)_options.Timeout.TotalMilliseconds }), captured.Task, timeout.Token).ConfigureAwait(false);
            workspace.PageReady();
            Observe("page_ready", phase.Elapsed, !created); phase.Restart();
            if (warmUp) { success = true; return null; }
            if (_options.UiFingerprintObserved is { } observe)
            {
                var structure = await page.EvaluateAsync<string>("""
                    () => {
                      const markers = [...document.querySelectorAll('[data-testid],[data-test-id],[role]')]
                        .flatMap(e => ['data-testid','data-test-id','role'].map(a => e.getAttribute(a)))
                        .filter(v => v && /^[a-zA-Z0-9_-]{1,80}$/.test(v));
                      const assets = [...document.querySelectorAll('script[src],link[href]')].flatMap(e => {
                        try { const u = new URL(e.src || e.href); return u.protocol === 'https:' && ['chatgpt.com','cdn.oaistatic.com'].includes(u.hostname) && /\.(js|css)$/.test(u.pathname) ? [u.origin + u.pathname] : []; }
                        catch { return []; }
                      });
                      return JSON.stringify({markers:[...new Set(markers)].sort(),assets:[...new Set(assets)].sort()});
                    }
                    """).WaitAsync(timeout.Token).ConfigureAwait(false);
                observe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(structure))).ToLowerInvariant());
            }
            ReportProgress("Message composer ready. Obtaining a fresh Sentinel handshake.");
            // This unsent draft triggers the normal frontend handshake. Its generation request is intercepted and aborted.
            workspace.NeedsNavigation = true;
            await input.FillAsync("Prepare SDK connection.").WaitAsync(timeout.Token).ConfigureAwait(false);
            workspace.CaptureHeaders = true;
            if (_options.SendButtonSelector is { } send) await page.Locator(send).ClickAsync(new() { Timeout = (float)_options.Timeout.TotalMilliseconds }).WaitAsync(timeout.Token).ConfigureAwait(false);
            else await input.PressAsync("Enter").WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_options.DiagnosticScreenshotPath is not null || _options.DiagnosticComposerPath is not null)
            {
                await Task.WhenAny(captured.Task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token)).ConfigureAwait(false);
                if (!captured.Task.IsCompleted) await CaptureDiagnosticsAsync(page).ConfigureAwait(false);
            }
            var headers = await captured.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            Observe("handshake", phase.Elapsed, !created);
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
            var browserCookies = await context.CookiesAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            var exportedCookies = browserCookies.Where(c => c.Domain is "chatgpt.com" or ".chatgpt.com").Select(c => new WebCookie
            {
                Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly,
                HostOnly = !c.Domain.StartsWith('.'), ExpiresAt = c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires) : null,
                SameSite = c.SameSite switch { SameSiteAttribute.Strict => "strict", SameSiteAttribute.Lax => "lax", _ => "no_restriction" }
            }).ToArray();
            var cookieHeader = string.Join("; ", (await context.CookiesAsync(["https://chatgpt.com/"]).WaitAsync(timeout.Token).ConfigureAwait(false)).Select(c => c.Name + "=" + c.Value));
            var stable = new Dictionary<string, string>(credentials.Headers, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in headers)
                if (pair.Key.StartsWith("oai-", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("oai-echo-logs", StringComparison.OrdinalIgnoreCase) || pair.Key.StartsWith("sec-ch-", StringComparison.OrdinalIgnoreCase) || pair.Key is "chatgpt-account-id" or "accept-language" or "originator" or "x-oai-mcp-form-version" or "x-openai-web-frontend" or "x-openai-codex-window-type") stable[pair.Key] = pair.Value;
            var sentinel = new WebSentinelSession { Token = token, IsPrepareToken = isPrepareToken, ExpiresAt = expires is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow.AddMinutes(2),
                ProofToken = headers.GetValueOrDefault("openai-sentinel-proof-token"), TurnstileToken = headers.GetValueOrDefault("openai-sentinel-turnstile-token"), ObserverToken = headers.GetValueOrDefault("openai-sentinel-so-token"), EchoLogs = headers.GetValueOrDefault("oai-echo-logs") };
            var refreshed = new WebCredentials { AccessToken = accessToken, CookieHeader = cookieHeader, UserAgent = headers.GetValueOrDefault("user-agent") ?? credentials.UserAgent,
                Cookies = exportedCookies, ExpiresAt = WebAuthentication.ReadAccessTokenExpiry(accessToken) ?? credentials.ExpiresAt, Headers = stable, SentinelSession = sentinel };
            workspace.Refreshed(refreshed);
            success = true;
            if (_options.ReuseSession && _options.PreloadNextTurn) workspace.BeginPreload(_options.Timeout);
            ReportProgress(_options.ReuseSession ? "Fresh Sentinel credentials obtained. Keeping the isolated session warm until its idle timeout." : "Sentinel credentials obtained. Closing the temporary browser page.");
            return new(refreshed, sentinel);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await CaptureDiagnosticsAsync(diagnosticPage).ConfigureAwait(false);
            throw new SdkException(challengeResponses > 0 ? "The browser kept waiting for Cloudflare until the configured timeout. Use a visible authenticated session and increase Browser.Timeout if more time is needed."
                : "Timed out obtaining Sentinel credentials. Complete sign-in in the browser or configure an external provider.",
                challengeResponses > 0 ? "sentinel_browser_challenge" : "sentinel_browser_timeout", System.Net.HttpStatusCode.ServiceUnavailable);
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
            if (diagnosticPage is not null && responseHandler is not null) diagnosticPage.Response -= responseHandler;
            if (lease is not null)
            {
                lease.Resource.Capture = null;
                lease.Resource.CaptureHeaders = false;
                if (!success) lease.Invalidate();
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            Observe(warmUp ? "warmup_total" : "total", total.Elapsed, !created);
        }
    }

    private void ReportProgress(string message)
    {
        try { _options.Progress?.Invoke(message); }
        catch { /* Progress observers cannot interrupt a challenge wait or authorization. */ }
    }

    private void Observe(string stage, TimeSpan elapsed, bool reused)
    {
        try { _options.TimingObserved?.Invoke(new(stage, elapsed, reused)); }
        catch { /* A timing observer must not change authorization or cleanup. */ }
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
