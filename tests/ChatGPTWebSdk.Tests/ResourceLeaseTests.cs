using ChatGPTWebSdk.Resources;

namespace ChatGPTWebSdk.Tests;

public sealed class ResourceLeaseTests
{
    private sealed class Resource : IAsyncDisposable
    {
        public int Closed;
        public bool FailClose;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Closed);
            return FailClose ? ValueTask.FromException(new IOException("Synthetic cleanup failure")) : ValueTask.CompletedTask;
        }
    }
    private static AsyncLeasePool<Resource> Pool(bool retain = true, int capacity = 2, TimeSpan? idle = null) => new(retain, idle ?? TimeSpan.FromMinutes(1), capacity);
    private static Task<Resource> Create(CancellationToken ct) => Task.FromResult(new Resource());

    [Fact]
    public async Task A_returned_resource_is_reused_and_closed_once_on_disposal()
    {
        var pool = Pool(); var first = await pool.AcquireAsync("one", Create, default); var resource = first.Resource;
        await first.DisposeAsync(); Assert.Equal(0, resource.Closed);
        await using (var next = await pool.AcquireAsync("one", _ => throw new Exception("Must reuse"), default)) Assert.Same(resource, next.Resource);
        await pool.DisposeAsync(); await pool.DisposeAsync(); Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Disabled_reuse_and_invalidated_leases_close_on_return()
    {
        await using var oneShot = Pool(retain: false);
        var first = await oneShot.AcquireAsync("one", Create, default); var resource = first.Resource;
        await first.DisposeAsync(); Assert.Equal(1, resource.Closed);
        await using var retained = Pool();
        var lease = await retained.AcquireAsync("one", Create, default); resource = lease.Resource;
        lease.Invalidate(); await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.Equal(1, resource.Closed);
        await using var next = await retained.AcquireAsync("one", Create, default); Assert.NotSame(resource, next.Resource);
    }

    [Fact]
    public async Task Concurrent_same_key_requests_share_one_exclusive_session()
    {
        await using var pool = Pool(); var first = await pool.AcquireAsync("one", Create, default);
        var second = pool.AcquireAsync("one", _ => throw new Exception("Must reuse"), default).AsTask();
        Assert.False(second.IsCompleted); var resource = first.Resource; await first.DisposeAsync();
        await using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Same(resource, acquired.Resource);
    }

    [Fact]
    public async Task A_cancelled_waiter_does_not_dispose_the_active_resource()
    {
        await using var pool = Pool(); await using var first = await pool.AcquireAsync("one", Create, default);
        using var cancel = new CancellationTokenSource();
        var second = pool.AcquireAsync("one", Create, cancel.Token).AsTask(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.Equal(0, first.Resource.Closed);
    }

    [Fact]
    public async Task Capacity_evicts_only_an_idle_session()
    {
        await using var pool = Pool(capacity: 1); var first = await pool.AcquireAsync("one", Create, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync("two", Create, default).AsTask());
        var old = first.Resource; await first.DisposeAsync();
        await using var next = await pool.AcquireAsync("two", Create, default); Assert.Equal(1, old.Closed); Assert.NotSame(old, next.Resource);
    }

    [Fact]
    public async Task Idle_expiry_closes_resources_without_waiting_for_another_request()
    {
        await using var pool = Pool(idle: TimeSpan.FromMilliseconds(60)); var first = await pool.AcquireAsync("one", Create, default);
        var resource = first.Resource; await first.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref resource.Closed) == 0) await Task.Delay(10, timeout.Token);
        Assert.Equal(1, resource.Closed);
    }

    [Fact]
    public async Task Expiry_does_not_close_active_leases()
    {
        await using var pool = Pool(idle: TimeSpan.FromMilliseconds(50)); await using var lease = await pool.AcquireAsync("one", Create, default);
        await Task.Delay(160); Assert.Equal(0, lease.Resource.Closed);
    }

    [Fact]
    public async Task Disposing_a_pool_defers_active_cleanup_until_return_and_rejects_waiters()
    {
        var pool = Pool(); var first = await pool.AcquireAsync("one", Create, default);
        var waiting = pool.AcquireAsync("one", Create, default).AsTask();
        await pool.DisposeAsync(); Assert.Equal(0, first.Resource.Closed);
        await first.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        Assert.Equal(1, first.Resource.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.AcquireAsync("new", Create, default).AsTask());
    }

    [Fact]
    public async Task Failed_factories_and_cleanup_release_the_slot()
    {
        await using var pool = Pool(capacity: 1);
        await Assert.ThrowsAsync<IOException>(() => pool.AcquireAsync("one", _ => Task.FromException<Resource>(new IOException()), default).AsTask());
        var first = await pool.AcquireAsync("two", Create, default); first.Resource.FailClose = true; first.Invalidate();
        await Assert.ThrowsAsync<IOException>(() => first.DisposeAsync().AsTask());
        await using var next = await pool.AcquireAsync("three", Create, default); Assert.Equal(0, next.Resource.Closed);
    }

    [Fact]
    public async Task Disposing_all_idle_sessions_attempts_every_cleanup_even_when_one_fails()
    {
        var pool = Pool(); var first = await pool.AcquireAsync("one", Create, default); var second = await pool.AcquireAsync("two", Create, default);
        first.Resource.FailClose = true; var a = first.Resource; var b = second.Resource;
        await first.DisposeAsync(); await second.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => pool.DisposeAsync().AsTask()); Assert.Equal(1, a.Closed); Assert.Equal(1, b.Closed);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task A_factory_finishing_after_cancellation_or_disposal_cannot_leak_its_resource(bool dispose)
    {
        await using var pool = Pool(); using var cancellation = new CancellationTokenSource();
        var creation = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = pool.AcquireAsync("one", _ => creation.Task, cancellation.Token).AsTask();
        if (dispose) await pool.DisposeAsync(); else cancellation.Cancel();
        var resource = new Resource(); creation.SetResult(resource);
        if (dispose) await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, resource.Closed);
    }

    [Theory, InlineData(0, 2), InlineData(-1, 2), InlineData(3601, 2), InlineData(60, 0), InlineData(60, 257)]
    public void Invalid_session_limits_cannot_create_a_pool(int seconds, int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Pool(capacity: capacity, idle: TimeSpan.FromSeconds(seconds)));
}
