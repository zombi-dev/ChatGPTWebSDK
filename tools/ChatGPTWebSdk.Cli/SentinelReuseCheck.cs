using System.Text.Json;
using ChatGPTWebSdk.Browser;
using ChatGPTWebSdk.Protocol;
using ChatGPTWebSdk.Web;

/// <summary>Offline verification using real Chromium and a synthetic frontend. Never contacts ChatGPT.</summary>
internal static class SentinelReuseCheck
{
    internal static async Task RunAsync(string browserDirectory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var measurements = new List<BrowserSentinelTiming>();
        var options = new BrowserSentinelOptions
        {
            BundledBrowserDirectory = Path.GetFullPath(browserDirectory), Headless = true, ReuseSession = true, MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(30), TimingObserved = measurements.Add
        };
        var creations = 0; var navigations = 0; var prepares = 0; var unexpected = 0;
        var credentials = new WebCredentials { AccessToken = "synthetic-offline-verification" };
        async Task<BrowserSentinelWorkspace> Create(WebCredentials value, CancellationToken ct)
        {
            creations++;
            var workspace = await BrowserSentinelWorkspace.CreateAsync(options, value, ct);
            try
            {
                await workspace.Context.RouteAsync("**/*", async route =>
                {
                    var uri = new Uri(route.Request.Url);
                    if (uri.Host == "chatgpt.com" && uri.AbsolutePath == "/" && route.Request.Method == "GET")
                    {
                        navigations++;
                        if (navigations >= 4) await Task.Delay(200);
                        if (navigations <= 2)
                        {
                            await route.FulfillAsync(new() { Status = 403, ContentType = "text/html", Headers = new Dictionary<string, string> { ["cf-mitigated"] = "challenge" },
                                Body = "<html><head><link rel='icon' href='data:,'></head><body>Offline challenge wait fixture<script>setTimeout(() => location.reload(), 70)</script></body></html>" });
                            return;
                        }
                        await route.FulfillAsync(new() { ContentType = "text/html", Body = """
                            <html><head><link rel='icon' href='data:,'></head><body><div id="prompt-textarea" role="textbox" contenteditable="true"></div><script>
                            document.querySelector('#prompt-textarea').addEventListener('keydown', async e => {
                              if (e.key !== 'Enter') return;
                              e.preventDefault();
                              await fetch('/backend-api/sentinel/chat-requirements/prepare', {method:'POST'});
                              fetch('/backend-api/f/conversation?fixture=1', {method:'POST',headers:{
                                'authorization':'Bearer synthetic-offline-verification',
                                'openai-sentinel-chat-requirements-token':'synthetic-' + crypto.randomUUID(),
                                'openai-sentinel-proof-token':'synthetic-' + crypto.randomUUID()
                              },body:'{}'}).catch(() => {});
                            });</script></body></html>
                            """ }); return;
                    }
                    if (uri.Host == "chatgpt.com" && uri.AbsolutePath == "/backend-api/sentinel/chat-requirements/prepare")
                    {
                        prepares++;
                        await route.FulfillAsync(new() { ContentType = "application/json", Body = "{\"expire_at\":" + DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds() + "}" }); return;
                    }
                    unexpected++;
                    await route.AbortAsync();
                }).WaitAsync(ct);
                return workspace;
            }
            catch { await workspace.DisposeAsync(); throw; }
        }
        await using var provider = new BrowserSentinelProvider(options, Create);
        await provider.WarmUpAsync("offline", credentials, deadline.Token);
        if (prepares != 0) throw new SdkException("Offline warm-up requested a challenge answer.", "offline_warmup_failed");
        var first = await provider.GetSessionAsync("offline", credentials, new(), deadline.Token);
        // Simulate time spent generating via HTTP. The next composer loads concurrently, without new proofs.
        await Task.Delay(250, deadline.Token);
        var second = await provider.GetSessionAsync("offline", first.Credentials, new(), deadline.Token);
        await provider.WarmUpAsync("offline", second.Credentials, deadline.Token);
        if (creations != 1 || prepares != 2 || unexpected != 0 || navigations != 5 || first.Sentinel.Token == second.Sentinel.Token || first.Sentinel.ProofToken == second.Sentinel.ProofToken)
            throw new SdkException($"Offline reuse verification failed: sessions={creations}, handshakes={prepares}, unexpectedRequests={unexpected}, navigations={navigations}.", "offline_reuse_failed");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test = "offline-sentinel-reuse", sessions = creations, navigations, handshakes = prepares, unexpectedRequests = unexpected,
            warmupMilliseconds = measurements.First(t => t.Stage == "warmup_total").Elapsed.TotalMilliseconds,
            warmSendMilliseconds = measurements.Where(t => t.Stage == "total").Select(t => t.Elapsed.TotalMilliseconds).ToArray(),
            freshAnswers = true, preloadedNextComposer = true, repeatedChallengeWait = true, remoteGenerationSent = false
        }));
    }
}
