using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ChatGPTWebSdk.Protocol;
using Microsoft.Playwright;

namespace ChatGPTWebSdk.Browser;

internal sealed class OwnedBrowser(IBrowser browser, Process? process = null, string? profile = null) : IAsyncDisposable
{
    internal IBrowser Browser { get; } = browser;
    private static readonly string ProfileRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser"));

    internal static async Task<OwnedBrowser> LaunchAsync(IPlaywright playwright, BrowserSentinelOptions options, CancellationToken ct)
    {
        if (!options.UseDesktopLauncher)
            return new(await playwright.Chromium.LaunchAsync(new() { Headless = false,
                Channel = options.ExecutablePath is null ? options.Channel : null, ExecutablePath = options.ExecutablePath }).WaitAsync(ct).ConfigureAwait(false));
        var executable = ResolveExecutable(playwright.Chromium.ExecutablePath, options);
        if (!File.Exists(executable))
            throw new SdkException("No installed Chromium browser was found. Configure Browser.ExecutablePath or explicitly install Playwright Chromium.", "sentinel_browser_unavailable", HttpStatusCode.ServiceUnavailable);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var profile = Path.Combine(ProfileRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var start = CreateStartInfo(executable, profile, port);
        Process? process = null;
        IBrowser? browser = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the temporary browser.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new SdkException("The desktop browser closed before its local connection was ready.", "sentinel_browser_failed", HttpStatusCode.ServiceUnavailable);
                try
                {
                    using var response = await http.GetAsync(new Uri(endpoint, "json/version"), ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint.AbsoluteUri,
                new() { Timeout = (float)options.Timeout.TotalMilliseconds }).WaitAsync(ct).ConfigureAwait(false);
            return new(browser, process, profile);
        }
        catch
        {
            if (browser is not null) try { await browser.CloseAsync().ConfigureAwait(false); } catch (PlaywrightException) { }
            await StopProcessAsync(process).ConfigureAwait(false);
            await CleanupProfileAsync(profile).ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string profile, int port)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--remote-debugging-address=127.0.0.1", $"--remote-debugging-port={port}",
            $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--no-startup-window" }) start.ArgumentList.Add(argument);
        return start;
    }

    private static string ResolveExecutable(string bundled, BrowserSentinelOptions options)
    {
        if (options.ExecutablePath is { } explicitPath) return Path.GetFullPath(explicitPath);
        var channel = options.Channel;
        if (channel is not (null or "chrome" or "msedge"))
            throw new ArgumentException("The desktop launcher supports chrome, msedge, or an explicit ExecutablePath. UseDesktopLauncher=false enables Playwright's other channels.");
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            if (channel != "msedge") candidates.AddRange(roots.Select(root => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe")));
            if (channel != "chrome") candidates.AddRange(roots.Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe")));
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (channel != "msedge") candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            if (channel != "chrome") candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
        }
        else
        {
            if (channel != "msedge") candidates.AddRange(["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable"]);
            if (channel != "chrome") candidates.AddRange(["/usr/bin/microsoft-edge", "/usr/bin/microsoft-edge-stable"]);
        }
        return candidates.FirstOrDefault(File.Exists) ?? (channel is null ? bundled : throw new SdkException("The configured browser channel is not installed. Set Browser.ExecutablePath.", "sentinel_browser_unavailable", HttpStatusCode.ServiceUnavailable));
    }

    private static async Task StopProcessAsync(Process? owned)
    {
        if (owned is null) return;
        try
        {
            if (!owned.HasExited) owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        finally { owned.Dispose(); }
    }

    internal static bool DeleteProfile(string ownedProfile)
    {
        var path = Path.GetFullPath(ownedProfile);
        if (!path.StartsWith(ProfileRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || Path.GetFileName(path).Length != 32 || !Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            throw new InvalidOperationException("Refusing to remove a directory outside the SDK's owned browser profiles.");
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static async Task CleanupProfileAsync(string ownedProfile)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (DeleteProfile(ownedProfile)) return;
            // Browser close can precede release of its child processes' metrics/cache handles.
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
        throw new SdkException("The temporary browser closed, but its owned profile remains locked. Release the lock and retry cleanup.", "sentinel_browser_cleanup_failed", HttpStatusCode.ServiceUnavailable);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Browser.CloseAsync().ConfigureAwait(false); }
        catch (PlaywrightException) { }
        finally
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            if (profile is not null) await CleanupProfileAsync(profile).ConfigureAwait(false);
        }
    }
}
