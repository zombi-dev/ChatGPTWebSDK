using ChatGPTWebSdk.Browser;

namespace ChatGPTWebSdk.Tests;

public sealed class BrowserLauncherTests
{
    [Fact]
    public void Desktop_launch_uses_structured_arguments_and_a_nonzero_loopback_port()
    {
        var start = OwnedBrowser.CreateStartInfo("browser with spaces.exe", "profile with spaces", 19222);
        Assert.False(start.UseShellExecute);
        Assert.False(start.CreateNoWindow);
        Assert.Contains("--remote-debugging-address=127.0.0.1", start.ArgumentList);
        Assert.Contains("--remote-debugging-port=19222", start.ArgumentList);
        Assert.Contains("--user-data-dir=profile with spaces", start.ArgumentList);
        Assert.DoesNotContain("--enable-automation", start.ArgumentList);
        Assert.DoesNotContain("--headless", start.ArgumentList);
        Assert.True(new BrowserSentinelOptions().UseDesktopLauncher);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Invalid_debug_ports_are_rejected_before_launch(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnedBrowser.CreateStartInfo("browser", "profile", port));

    [Fact]
    public void Profile_cleanup_removes_only_a_generated_owned_directory()
    {
        var profile = Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "synthetic-state.txt"), "synthetic-state");
        OwnedBrowser.DeleteProfile(profile);
        Assert.False(Directory.Exists(profile));
        Assert.Throws<InvalidOperationException>(() => OwnedBrowser.DeleteProfile(Path.GetTempPath()));
        Assert.Throws<InvalidOperationException>(() => OwnedBrowser.DeleteProfile(Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser")));
        Assert.Throws<InvalidOperationException>(() => OwnedBrowser.DeleteProfile(Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser", "not-an-owned-id")));
    }

    [Fact]
    public async Task Profile_cleanup_waits_for_an_owned_file_lock_to_release()
    {
        var profile = Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var locked = new FileStream(Path.Combine(profile, "synthetic-state.txt"), FileMode.Create, FileAccess.Write, FileShare.None);
        var cleanup = OwnedBrowser.CleanupProfileAsync(profile);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        locked.Dispose();
        await cleanup;
        Assert.False(Directory.Exists(profile));
    }
}
