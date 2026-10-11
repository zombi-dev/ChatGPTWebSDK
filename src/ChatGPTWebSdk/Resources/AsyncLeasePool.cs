namespace ChatGPTWebSdk.Resources;

/// <summary>Bounded, exclusive leases. Idle resources expire without keeping a process alive indefinitely.</summary>
internal sealed class AsyncLeasePool<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    internal sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal T? Resource;
        internal int Users;
        internal DateTimeOffset ReleasedAt;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly int _capacity;
    private readonly bool _retain;
    private readonly Timer _timer;
    private Task _cleanup = Task.CompletedTask;
    private bool _disposed;

    internal AsyncLeasePool(bool retain, TimeSpan idleTimeout, int capacity)
    {
        if (idleTimeout <= TimeSpan.Zero || idleTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (capacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        _retain = retain; _idleTimeout = idleTimeout; _capacity = capacity;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(idleTimeout.TotalMilliseconds / 2, 50, 1000));
        _timer = new(_ => Sweep(), null, interval, interval);
    }

    internal async ValueTask<Lease> AcquireAsync(string key, Func<CancellationToken, Task<T>> create, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Entry entry;
        T? evicted = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                if (_entries.Count >= _capacity)
                {
                    var idle = _entries.Where(p => p.Value.Users == 0).OrderBy(p => p.Value.ReleasedAt).FirstOrDefault();
                    if (idle.Value is null) throw new InvalidOperationException("All reusable sessions are busy. Increase the session limit or wait for a lease to finish.");
                    _entries.Remove(idle.Key); evicted = idle.Value.Resource; idle.Value.Gate.Dispose();
                }
                _entries.Add(key, entry = new());
            }
            entry.Users++;
        }
        var acquired = false;
        try
        {
            if (evicted is not null) await evicted.DisposeAsync().ConfigureAwait(false);
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            entry.Resource ??= await create(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            return new(this, key, entry, entry.Resource);
        }
        catch
        {
            if (acquired) await ReleaseAsync(key, entry, invalidate: true).ConfigureAwait(false);
            else DropUser(key, entry);
            throw;
        }
    }

    private void DropUser(string key, Entry entry)
    {
        lock (_sync)
        {
            entry.Users--;
            if (entry.Users == 0 && entry.Resource is null)
            { _entries.Remove(key); entry.Gate.Dispose(); }
        }
    }

    private async ValueTask ReleaseAsync(string key, Entry entry, bool invalidate)
    {
        T? close = null;
        lock (_sync)
        {
            if (invalidate || !_retain || _disposed) { close = entry.Resource; entry.Resource = null; }
        }
        try { if (close is not null) await close.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                entry.ReleasedAt = DateTimeOffset.UtcNow;
                entry.Users--; entry.Gate.Release();
                if (entry.Users == 0 && entry.Resource is null)
                { _entries.Remove(key); entry.Gate.Dispose(); }
            }
        }
    }

    private void Sweep()
    {
        lock (_sync)
        {
            if (_disposed || !_cleanup.IsCompleted) return;
            var expired = _entries.Where(p => p.Value.Users == 0 && DateTimeOffset.UtcNow - p.Value.ReleasedAt >= _idleTimeout).ToArray();
            foreach (var pair in expired) { _entries.Remove(pair.Key); pair.Value.Gate.Dispose(); }
            // Observe cleanup errors here; explicit lease/runtime disposal still reports errors to its caller.
            _cleanup = CloseIdleAsync(expired.Select(p => p.Value.Resource).OfType<T>());
        }
    }

    private static async Task CloseIdleAsync(IEnumerable<T> resources)
    {
        foreach (var resource in resources)
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch { /* Expiry cleanup must not crash the timer thread. */ }
    }

    public async ValueTask DisposeAsync()
    {
        T[] idle;
        Task cleanup;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _timer.Dispose(); cleanup = _cleanup;
            var entries = _entries.Where(p => p.Value.Users == 0).ToArray();
            idle = entries.Select(p => p.Value.Resource).OfType<T>().ToArray();
            foreach (var pair in entries) { _entries.Remove(pair.Key); pair.Value.Gate.Dispose(); }
            // Active leases are closed when returned; waiting acquisitions reject the disposed pool.
        }
        try { await Task.WhenAll(idle.Select(async resource => await resource.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false); }
        finally { await cleanup.ConfigureAwait(false); }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private readonly AsyncLeasePool<T> _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private int _returned;
        private bool _invalidate;
        internal T Resource { get; }
        internal Lease(AsyncLeasePool<T> owner, string key, Entry entry, T resource)
        { _owner = owner; _key = key; _entry = entry; Resource = resource; }
        internal void Invalidate() => _invalidate = true;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _returned, 1) == 0
            ? _owner.ReleaseAsync(_key, _entry, _invalidate) : ValueTask.CompletedTask;
    }
}
