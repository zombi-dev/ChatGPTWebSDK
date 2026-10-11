using System.Security.Cryptography;
using System.Text.Json;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;
using Microsoft.Playwright;

namespace ChatGPTWebSdk.Browser;

/// <summary>One isolated account session; contains no reusable challenge answers.</summary>
internal sealed class BrowserSentinelWorkspace : IAsyncDisposable
{
    private readonly IPlaywright _driver;
    private readonly OwnedBrowser? _owned;
    private readonly string _initialIdentity;
    private string _refreshedIdentity;
    private Task? _navigation;
    internal IBrowserContext Context { get; }
    internal IPage Page { get; }
    internal bool NeedsNavigation { get; set; } = true;
    internal TaskCompletionSource<IReadOnlyDictionary<string, string>>? Capture { get; set; }
    internal bool CaptureHeaders { get; set; }

    internal BrowserSentinelWorkspace(IPlaywright driver, OwnedBrowser? owned, IBrowserContext context, IPage page, WebCredentials credentials)
    {
        _driver = driver; _owned = owned; Context = context; Page = page;
        _initialIdentity = _refreshedIdentity = Identity(credentials);
        page.Close += (_, _) => Capture?.TrySetException(new PlaywrightException("The Sentinel page was closed before the handshake completed."));
        page.Crash += (_, _) => Capture?.TrySetException(new PlaywrightException("The Sentinel page stopped before the handshake completed."));
    }

    internal bool Matches(WebCredentials credentials) => !Page.IsClosed && Identity(credentials) is var identity && (identity == _initialIdentity || identity == _refreshedIdentity);
    internal void Refreshed(WebCredentials credentials) => _refreshedIdentity = Identity(credentials);
    internal Task NavigateAsync(TimeSpan timeout) => _navigation ??= Page.GotoAsync("https://chatgpt.com/", new()
    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = (float)timeout.TotalMilliseconds });
    internal void PageReady() { NeedsNavigation = false; _navigation = null; }
    internal void BeginPreload(TimeSpan timeout)
    {
        // Only page loading is overlapped with the API turn. The next send still requests its own fresh proof.
        var pending = NavigateAsync(timeout);
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    internal static string Identity(WebCredentials credentials) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        credentials.AccessToken, credentials.CookieHeader,
        Cookies = credentials.Cookies.OrderBy(c => c.Domain, StringComparer.Ordinal).ThenBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToArray(),
        Headers = credentials.Headers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray()
    })));

    internal static async Task<BrowserSentinelWorkspace> CreateAsync(BrowserSentinelOptions options, WebCredentials credentials, CancellationToken ct)
    {
        var driver = await BrowserDiagnostics.AwaitDriverAsync(Playwright.CreateAsync(), ct).ConfigureAwait(false);
        OwnedBrowser? owned = null;
        IBrowserContext? context = null;
        try
        {
            IBrowser browser;
            if (options.CdpEndpoint is { } address)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https" or "ws" or "wss") || endpoint.UserInfo.Length > 0)
                    throw new ArgumentException("The existing-browser CDP endpoint must be a loopback HTTP(S) or WebSocket address without user information.");
                browser = await driver.Chromium.ConnectOverCDPAsync(address, new() { Timeout = (float)options.Timeout.TotalMilliseconds }).WaitAsync(ct).ConfigureAwait(false);
            }
            else { owned = await OwnedBrowser.LaunchAsync(driver, options, ct).ConfigureAwait(false); browser = owned.Browser; }
            context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block, ViewportSize = ViewportSize.NoViewport }).WaitAsync(ct).ConfigureAwait(false);
            if (credentials.Cookies.Count > 0)
                await context.AddCookiesAsync(credentials.Cookies.Where(c => c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow).Select(BrowserSentinelProvider.ToBrowserCookie)).WaitAsync(ct).ConfigureAwait(false);
            else if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                await context.AddCookiesAsync(credentials.CookieHeader.Split(';').Select(pair => pair.Trim().Split('=', 2)).Where(pair => pair.Length == 2)
                    .Select(pair => new Cookie { Name = pair[0], Value = pair[1], Url = "https://chatgpt.com/", Secure = true })).WaitAsync(ct).ConfigureAwait(false);
            else if (options.CdpEndpoint is not null && browser.Contexts.FirstOrDefault(c => c != context) is { } signedIn)
                await context.AddCookiesAsync((await signedIn.CookiesAsync(["https://chatgpt.com/"]).WaitAsync(ct).ConfigureAwait(false)).Select(c => new Cookie
                { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly, SameSite = c.SameSite })).WaitAsync(ct).ConfigureAwait(false);
            var page = await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
            var workspace = new BrowserSentinelWorkspace(driver, owned, context, page, credentials);
            // Keep the guard installed while idle too: this SDK page cannot send a browser-generated turn.
            async Task InterceptAsync(IRoute route)
            {
                if (route.Request.Method != "POST" || !new Uri(route.Request.Url).AbsolutePath.EndsWith("/conversation", StringComparison.Ordinal))
                { await route.FallbackAsync().ConfigureAwait(false); return; }
                var headers = await route.Request.AllHeadersAsync().ConfigureAwait(false);
                await route.AbortAsync("aborted").ConfigureAwait(false);
                if (workspace.CaptureHeaders) workspace.Capture?.TrySetResult(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            }
            await page.RouteAsync("https://chatgpt.com/backend-api/**", InterceptAsync).WaitAsync(ct).ConfigureAwait(false);
            return workspace;
        }
        catch
        {
            try { if (context is not null) await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            finally { try { if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false); } finally { driver.Dispose(); } }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Capture = null;
        try { await Context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (PlaywrightException) { }
        finally { try { if (_owned is not null) await _owned.DisposeAsync().ConfigureAwait(false); } finally { _driver.Dispose(); } }
    }
}
