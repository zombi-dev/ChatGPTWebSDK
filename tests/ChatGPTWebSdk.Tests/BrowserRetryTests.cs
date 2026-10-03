using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

namespace ChatGPTWebSdk.Tests;

public sealed class BrowserRetryTests
{
    private static readonly WebAuthorizedSession Session = new(new(), new() { Token = "synthetic-requirements", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });

    [Fact]
    public async Task Successful_handshake_stops_after_transient_startup_retries()
    {
        var attempts = 0;
        var result = await SentinelBootstrapRetry.RunAsync(new() { RetryDelay = TimeSpan.Zero }, _ =>
            ++attempts < 3 ? Task.FromException<WebAuthorizedSession>(new SdkException("Transient startup failure", "sentinel_browser_challenge")) : Task.FromResult(Session), default);
        Assert.Same(Session, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Default_policy_limits_retries_to_three_and_preserves_the_final_error()
    {
        var attempts = 0;
        var error = new SdkException("Transient startup failure", "sentinel_browser_failed");
        var result = await Assert.ThrowsAsync<SdkException>(() => SentinelBootstrapRetry.RunAsync(new() { RetryDelay = TimeSpan.Zero }, _ =>
        { attempts++; return Task.FromException<WebAuthorizedSession>(error); }, default));
        Assert.Same(error, result);
        Assert.Equal(4, attempts);
        Assert.Equal(TimeSpan.FromSeconds(5), new BrowserSentinelOptions().RetryDelay);
    }

    [Theory]
    [InlineData("authentication_account_mismatch")]
    [InlineData("sentinel_headers_missing")]
    [InlineData("upstream_http_error")]
    public async Task Account_protocol_and_upstream_errors_are_not_retried(string code)
    {
        var attempts = 0;
        var error = new SdkException("Failure", code);
        var result = await Assert.ThrowsAsync<SdkException>(() => SentinelBootstrapRetry.RunAsync(new() { RetryDelay = TimeSpan.Zero }, _ =>
        { attempts++; return Task.FromException<WebAuthorizedSession>(error); }, default));
        Assert.Same(error, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_during_the_retry_interval_prevents_another_browser()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SentinelBootstrapRetry.RunAsync(new() { Progress = _ => cancellation.Cancel() }, _ =>
        { attempts++; return Task.FromException<WebAuthorizedSession>(new SdkException("Timeout", "sentinel_browser_timeout")); }, cancellation.Token));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task Invalid_retry_limits_cannot_start_a_browser(int retries)
    {
        var attempts = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SentinelBootstrapRetry.RunAsync(new() { MaxRetries = retries }, _ =>
        { attempts++; return Task.FromResult(Session); }, default));
        Assert.Equal(0, attempts);
    }
}
