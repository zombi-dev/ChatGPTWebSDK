using Microsoft.Playwright;

namespace ChatGPTWebSdk.Browser;

public sealed record BrowserDiagnosticResult(string BrowserVersion, bool Headless, bool SoftwareRendering, bool? HardwareAcceleration);

/// <summary>Checks the actual SDK-owned browser launcher, driver, JavaScript and canvas rendering without accessing ChatGPT or authentication.</summary>
public static class BrowserDiagnostics
{
    public static async Task<BrowserDiagnosticResult> VerifyAsync(BrowserSentinelOptions? options = null, CancellationToken ct = default)
    {
        options ??= new() { Headless = true };
        if (options.CdpEndpoint is not null) throw new ArgumentException("Diagnostics launches an owned browser; omit CdpEndpoint.", nameof(options));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await OwnedBrowser.LaunchAsync(playwright, options, timeout.Token).ConfigureAwait(false);
        await using var context = await browser.Browser.NewContextAsync(new() { ViewportSize = ViewportSize.NoViewport }).WaitAsync(timeout.Token).ConfigureAwait(false);
        var page = await context.NewPageAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        var valid = await page.EvaluateAsync<bool>("() => { const c = document.createElement('canvas'); c.width = c.height = 2; const g = c.getContext('2d'); if (!g) return false; g.fillStyle = 'rgb(12,34,56)'; g.fillRect(0,0,2,2); const p = g.getImageData(0,0,1,1).data; return 1+1 === 2 && p[0] === 12 && p[1] === 34 && p[2] === 56 && p[3] === 255; }").WaitAsync(timeout.Token).ConfigureAwait(false);
        if (!valid) throw new InvalidOperationException("The owned browser failed JavaScript or canvas rendering.");
        bool? hardware = null;
        var session = await browser.Browser.NewBrowserCDPSessionAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        try { hardware = OwnedBrowser.HasHardwareAcceleration(await session.SendAsync("SystemInfo.getInfo").WaitAsync(timeout.Token).ConfigureAwait(false)); }
        catch (PlaywrightException) { }
        finally { await session.DetachAsync().ConfigureAwait(false); }
        return new(browser.Browser.Version, options.Headless, browser.SoftwareRendering, hardware);
    }
}
