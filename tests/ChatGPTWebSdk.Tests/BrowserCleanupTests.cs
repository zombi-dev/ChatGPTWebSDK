using System.Diagnostics;
using System.Reflection;
using ChatGPTWebSdk.Browser;
using Microsoft.Playwright;

namespace ChatGPTWebSdk.Tests;

public sealed class BrowserCleanupTests
{
    public class BrowserStub : DispatchProxy
    {
        public Func<Task> Close { get; set; } = () => Task.CompletedTask;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IBrowser.CloseAsync) ? Close() : throw new NotSupportedException(method?.Name);
    }

    public class DriverStub : DispatchProxy
    {
        public Action Disposed { get; set; } = () => { };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IDisposable.Dispose)) throw new NotSupportedException(method?.Name);
            Disposed(); return null;
        }
    }

    [Fact]
    public async Task Normal_close_completes_and_removes_only_its_owned_profile()
    {
        var browser = DispatchProxy.Create<IBrowser, BrowserStub>();
        var calls = 0; ((BrowserStub)browser).Close = () => { calls++; return Task.CompletedTask; };
        var profile = CreateProfile();
        await new OwnedBrowser(browser, profile: profile).DisposeAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls); Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public async Task Disconnected_browser_still_cleans_its_owned_profile()
    {
        var browser = DispatchProxy.Create<IBrowser, BrowserStub>();
        ((BrowserStub)browser).Close = () => Task.FromException(new PlaywrightException("Synthetic closed connection"));
        var profile = CreateProfile();
        await new OwnedBrowser(browser, profile: profile).DisposeAsync(TimeSpan.FromSeconds(1));
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public async Task Unexpected_close_failure_is_reported_after_profile_cleanup()
    {
        var browser = DispatchProxy.Create<IBrowser, BrowserStub>();
        var failure = new InvalidOperationException("Synthetic cleanup failure");
        ((BrowserStub)browser).Close = () => Task.FromException(failure);
        var profile = CreateProfile();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OwnedBrowser(browser, profile: profile).DisposeAsync(TimeSpan.FromSeconds(1)).AsTask()));
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public async Task Stalled_close_times_out_then_terminates_the_real_owned_process_and_cleans_its_profile()
    {
        var browser = DispatchProxy.Create<IBrowser, BrowserStub>();
        ((BrowserStub)browser).Close = () => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add("setInterval(() => {}, 1000)");
        var process = Process.Start(start)!; var pid = process.Id; var profile = CreateProfile();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => new OwnedBrowser(browser, process, profile)
                .DisposeAsync(TimeSpan.FromMilliseconds(30)).AsTask());
            Assert.False(Directory.Exists(profile));
            Assert.False(IsRunning(pid));
        }
        finally
        {
            if (IsRunning(pid)) using (var leftover = Process.GetProcessById(pid)) leftover.Kill(entireProcessTree: true);
            if (Directory.Exists(profile)) OwnedBrowser.DeleteProfile(profile);
        }
    }

    [Fact]
    public async Task Ready_driver_is_returned_without_disposal()
    {
        var driver = DispatchProxy.Create<IPlaywright, DriverStub>(); var disposed = false;
        ((DriverStub)driver).Disposed = () => disposed = true;
        Assert.Same(driver, await BrowserDiagnostics.AwaitDriverAsync(Task.FromResult(driver), CancellationToken.None));
        Assert.False(disposed); driver.Dispose(); Assert.True(disposed);
    }

    [Fact]
    public async Task Driver_that_finishes_after_cancellation_is_disposed()
    {
        var driver = DispatchProxy.Create<IPlaywright, DriverStub>();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((DriverStub)driver).Disposed = () => disposed.TrySetResult();
        var creation = new TaskCompletionSource<IPlaywright>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrowserDiagnostics.AwaitDriverAsync(creation.Task, cancelled.Token));
        creation.SetResult(driver);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Driver_creation_failure_is_preserved()
    {
        var failure = new PlaywrightException("Synthetic driver failure");
        Assert.Same(failure, await Assert.ThrowsAsync<PlaywrightException>(() =>
            BrowserDiagnostics.AwaitDriverAsync(Task.FromException<IPlaywright>(failure), CancellationToken.None)));
    }

    private static string CreateProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "ChatGPTWebSdk.Browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile); File.WriteAllText(Path.Combine(profile, "synthetic-state.txt"), "synthetic"); return profile;
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
