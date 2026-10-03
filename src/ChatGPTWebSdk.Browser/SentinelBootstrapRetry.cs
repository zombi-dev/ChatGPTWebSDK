using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Browser;

internal static class SentinelBootstrapRetry
{
    internal static async Task<WebAuthorizedSession> RunAsync(BrowserSentinelOptions options,
        Func<CancellationToken, Task<WebAuthorizedSession>> acquire, CancellationToken ct)
    {
        if (options.MaxRetries is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(options.MaxRetries), "Use between zero and three Sentinel browser retries.");
        if (options.RetryDelay < TimeSpan.Zero || options.Timeout <= TimeSpan.Zero)
            throw new ArgumentException("Browser Timeout must be positive and RetryDelay must not be negative.", nameof(options));
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await acquire(ct).ConfigureAwait(false); }
            catch (SdkException ex) when (attempt < options.MaxRetries &&
                ex.Code is "sentinel_browser_timeout" or "sentinel_browser_failed" or "sentinel_browser_challenge")
            {
                // Acquire has already closed the owned context/browser. Only an unsent handshake is retried.
                ct.ThrowIfCancellationRequested();
                options.Progress?.Invoke($"Sentinel browser startup failed ({ex.Code}). Retrying {attempt + 1}/{options.MaxRetries} after {options.RetryDelay.TotalSeconds:g} seconds.");
                await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
