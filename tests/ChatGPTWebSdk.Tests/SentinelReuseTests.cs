using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;
using Microsoft.Playwright;

namespace ChatGPTWebSdk.Tests;

public sealed class SentinelReuseTests
{
    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Run { get; set; } = (method, _) => throw new NotSupportedException(method.Name);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Run(method!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> run) where T : class
    { var value = DispatchProxy.Create<T, Stub>(); ((Stub)(object)value).Run = run; return value; }
    private static WebCredentials Credentials(string token = "synthetic") => new() { AccessToken = token, CookieHeader = "session=synthetic" };

    private sealed class Fixture
    {
        public int Created, Closed, Navigations, Sends;
        public bool Challenge;
        public TaskCompletionSource? Composer;
        public TaskCompletionSource<IResponse?>? Preload;
        public bool FailPreload;
        public readonly TaskCompletionSource Navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BrowserSentinelWorkspace> Create(WebCredentials credentials, CancellationToken ct)
        {
            Created++;
            BrowserSentinelWorkspace? workspace = null;
            EventHandler<IResponse>? responses = null;
            var frame = Proxy<IFrame>((method, _) => throw new NotSupportedException(method.Name));
            var request = Proxy<IRequest>((method, _) => method.Name switch { "get_IsNavigationRequest" => true, "get_Frame" => frame, _ => throw new NotSupportedException(method.Name) });
            var response = Proxy<IResponse>((method, _) => method.Name switch
            { "get_Url" => "https://chatgpt.com/", "get_Status" => 403, "get_Request" => request, "HeaderValueAsync" => Task.FromResult<string?>("challenge"), _ => throw new NotSupportedException(method.Name) });
            var locator = Proxy<ILocator>((method, _) =>
            {
                if (method.Name == "WaitForAsync") return Composer?.Task ?? Task.CompletedTask;
                if (method.Name == "FillAsync") return Task.CompletedTask;
                if (method.Name is "PressAsync" or "ClickAsync")
                {
                    Sends++;
                    workspace!.Capture!.TrySetResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["openai-sentinel-chat-requirements-token"] = "synthetic-fresh-" + Sends, ["openai-sentinel-proof-token"] = "synthetic-proof-" + Sends, ["authorization"] = "Bearer " + credentials.AccessToken });
                    return Task.CompletedTask;
                }
                throw new NotSupportedException(method.Name);
            });
            var page = Proxy<IPage>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_IsClosed": return false;
                    case "get_MainFrame": return frame;
                    case "Locator": return locator;
                    case "add_Response": responses += (EventHandler<IResponse>)args![0]!; return null;
                    case "remove_Response": responses -= (EventHandler<IResponse>)args![0]!; return null;
                    case "add_Close": case "add_Crash": return null;
                    case "BringToFrontAsync": return Task.CompletedTask;
                    case "GotoAsync":
                        Navigations++;
                        if (Navigations > 1 && FailPreload) return Task.FromException<IResponse?>(new PlaywrightException("Synthetic navigation failure"));
                        if (Navigations > 1 && Preload is not null) return Preload.Task;
                        if (Challenge) for (var i = 0; i < 3; i++) responses?.Invoke(null, response);
                        Navigated.TrySetResult(); return Task.FromResult<IResponse?>(null);
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var context = Proxy<IBrowserContext>((method, _) => method.Name switch
            {
                "CloseAsync" => Close(),
                "CookiesAsync" => Task.FromResult<IReadOnlyList<BrowserContextCookiesResult>>([new()
                { Name = "session", Value = "synthetic", Domain = "chatgpt.com", Path = "/", Secure = true, HttpOnly = true, Expires = -1, SameSite = SameSiteAttribute.Lax }]),
                _ => throw new NotSupportedException(method.Name)
            });
            var driver = Proxy<IPlaywright>((method, _) => method.Name == "Dispose" ? null : throw new NotSupportedException(method.Name));
            workspace = new(driver, null, context, page, credentials);
            return Task.FromResult(workspace);
        }
        private Task Close() { Closed++; return Task.CompletedTask; }
    }
    private static BrowserSentinelOptions Options(bool reuse = true, Action<BrowserSentinelTiming>? timing = null) => new()
    { ReuseSession = reuse, PreloadNextTurn = false, Headless = true, Timeout = TimeSpan.FromSeconds(5), MaxRetries = 0, TimingObserved = timing };

    [Fact]
    public async Task Next_page_load_overlaps_the_API_turn_without_requesting_idle_proofs_or_duplicate_navigation()
    {
        var fixture = new Fixture { Preload = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = new BrowserSentinelProvider(new() { Headless = true, Timeout = TimeSpan.FromSeconds(5), MaxRetries = 0 }, fixture.Create);
        var first = await provider.GetSessionAsync("account", Credentials(), new());
        Assert.Equal(2, fixture.Navigations); Assert.Equal(1, fixture.Sends);
        var next = provider.GetSessionAsync("account", first.Credentials, new()).AsTask();
        Assert.False(next.IsCompleted); Assert.Equal(2, fixture.Navigations); Assert.Equal(1, fixture.Sends);
        fixture.Preload.SetResult(null); await next;
        Assert.Equal(2, fixture.Sends); Assert.Equal(3, fixture.Navigations); Assert.Equal(1, fixture.Created);
    }

    [Fact]
    public async Task Failed_preload_does_not_change_the_completed_handshake_and_is_recovered_on_the_next_attempt()
    {
        var fixture = new Fixture { FailPreload = true };
        await using var provider = new BrowserSentinelProvider(new() { Headless = true, MaxRetries = 0 }, fixture.Create);
        Assert.NotNull(await provider.GetSessionAsync("account", Credentials(), new()));
        await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", Credentials(), new()).AsTask());
        Assert.Equal(1, fixture.Sends); Assert.Equal(1, fixture.Closed);
        fixture.FailPreload = false;
        Assert.NotNull(await provider.GetSessionAsync("account", Credentials(), new())); Assert.Equal(2, fixture.Created);
    }

    [Fact]
    public async Task Consecutive_sends_reuse_the_browser_but_obtain_distinct_authorization()
    {
        var fixture = new Fixture(); var timings = new List<BrowserSentinelTiming>();
        await using (var provider = new BrowserSentinelProvider(Options(timing: timings.Add), fixture.Create))
        {
            var a = await provider.GetSessionAsync("account", Credentials(), new());
            var b = await provider.GetSessionAsync("account", Credentials(), new());
            Assert.NotEqual(a.Sentinel.Token, b.Sentinel.Token); Assert.NotEqual(a.Sentinel.ProofToken, b.Sentinel.ProofToken);
            Assert.Equal(1, fixture.Created); Assert.Equal(2, fixture.Navigations); Assert.Equal(0, fixture.Closed);
        }
        Assert.Equal(1, fixture.Closed);
        Assert.Equal(new[] { false, true }, timings.Where(t => t.Stage == "total").Select(t => t.ReusedSession));
    }

    [Fact]
    public async Task Warmup_loads_the_page_without_requesting_tokens_and_the_first_send_reuses_it()
    {
        var fixture = new Fixture(); await using var provider = new BrowserSentinelProvider(Options(), fixture.Create);
        await provider.WarmUpAsync("account", Credentials()); Assert.Equal(0, fixture.Sends);
        await provider.GetSessionAsync("account", Credentials(), new());
        Assert.Equal(1, fixture.Created); Assert.Equal(1, fixture.Navigations); Assert.Equal(1, fixture.Sends);
    }

    [Fact]
    public async Task Refreshed_browser_credentials_do_not_discard_their_own_warm_session()
    {
        var fixture = new Fixture(); await using var provider = new BrowserSentinelProvider(Options(), fixture.Create);
        var a = await provider.GetSessionAsync("account", Credentials(), new());
        await provider.GetSessionAsync("account", a.Credentials, new()); Assert.Equal(1, fixture.Created);
    }

    [Fact]
    public async Task Disabling_reuse_closes_each_session_before_returning()
    {
        var fixture = new Fixture(); await using var provider = new BrowserSentinelProvider(Options(reuse: false), fixture.Create);
        await provider.GetSessionAsync("account", Credentials(), new()); Assert.Equal(1, fixture.Closed);
        await provider.GetSessionAsync("account", Credentials(), new()); Assert.Equal(2, fixture.Created); Assert.Equal(2, fixture.Closed);
    }

    [Theory, InlineData("account"), InlineData("credentials")]
    public async Task Changed_accounts_or_authentication_do_not_reuse_an_unrelated_session(string change)
    {
        var fixture = new Fixture(); await using var provider = new BrowserSentinelProvider(Options(), fixture.Create);
        await provider.GetSessionAsync("account", Credentials(), new());
        await provider.GetSessionAsync(change == "account" ? "other" : "account", Credentials(change == "credentials" ? "synthetic-other" : "synthetic"), new());
        Assert.Equal(2, fixture.Created); Assert.Equal(change == "credentials" ? 1 : 0, fixture.Closed);
    }

    [Fact]
    public async Task Repeating_challenges_keep_the_same_browser_open_until_the_composer_recovers()
    {
        var fixture = new Fixture { Challenge = true, Composer = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = new BrowserSentinelProvider(Options(), fixture.Create);
        var pending = provider.GetSessionAsync("account", Credentials(), new()).AsTask();
        await fixture.Navigated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted); Assert.Equal(1, fixture.Created); Assert.Equal(0, fixture.Closed);
        fixture.Composer.SetResult(); Assert.NotNull(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Created); Assert.Equal(0, fixture.Closed);
    }

    [Fact]
    public async Task Challenge_waiting_honors_cancellation_without_restarting()
    {
        var fixture = new Fixture { Challenge = true, Composer = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = new BrowserSentinelProvider(Options(), fixture.Create);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetSessionAsync("account", Credentials(), new(), cancellation.Token).AsTask();
        await fixture.Navigated.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(1, fixture.Created); Assert.Equal(1, fixture.Closed);
    }

    [Fact]
    public async Task A_challenge_timeout_occurs_only_after_its_configured_wait_budget()
    {
        var fixture = new Fixture { Challenge = true, Composer = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var provider = new BrowserSentinelProvider(new() { Timeout = TimeSpan.FromMilliseconds(180), MaxRetries = 0 }, fixture.Create);
        var elapsed = Stopwatch.StartNew();
        Assert.Equal("sentinel_browser_challenge", (await Assert.ThrowsAsync<SdkException>(() => provider.GetSessionAsync("account", Credentials(), new()).AsTask())).Code);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(120)); Assert.Equal(1, fixture.Closed);
    }

    [Fact]
    public async Task Throwing_timing_observers_cannot_break_authorization()
    {
        var fixture = new Fixture(); await using var provider = new BrowserSentinelProvider(Options(timing: _ => throw new IOException("Synthetic observer failure")), fixture.Create);
        Assert.NotNull(await provider.GetSessionAsync("account", Credentials(), new()));
    }

    [Fact]
    public async Task Provider_disposal_rejects_later_sends()
    {
        var fixture = new Fixture(); var provider = new BrowserSentinelProvider(Options(), fixture.Create); await provider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.GetSessionAsync("account", Credentials(), new()).AsTask()); Assert.Equal(0, fixture.Created);
    }
}
