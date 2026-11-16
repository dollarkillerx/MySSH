using MySSH.Core;

namespace MySSH.Ssh;

/// <summary>Pool key: one chain per (target host, identity used on the target).</summary>
internal readonly record struct PoolKey(Guid HostId, Guid? IdentityId);

internal interface IPooledChain : IAsyncDisposable
{
    /// <summary>Alive, and acceptable for this origin (strict origins never reuse a chain trusted via AcceptOnce).</summary>
    bool IsReusableFor(ConnectionOrigin origin);
}

/// <summary>
/// Ref-counted pool. Every <see cref="PoolRef{T}"/> (a lease, a shell, an SFTP session, …) holds one reference; the last
/// release removes the entry and disposes the chain. Connects for the same key are serialized so concurrent callers share
/// one chain (and one host-key / passphrase prompt) instead of racing.
/// </summary>
internal sealed class ConnectionPool<TChain> where TChain : class, IPooledChain
{
    readonly Lock _lock = new();
    readonly Dictionary<PoolKey, List<Entry>> _entries = [];
    readonly Dictionary<PoolKey, SemaphoreSlim> _connectLocks = [];
    bool _disposed;

    internal sealed class Entry(ConnectionPool<TChain> pool, PoolKey key, TChain chain)
    {
        public PoolKey Key { get; } = key;
        public TChain Chain { get; } = chain;
        public int Refs; // guarded by pool._lock
        public ConnectionPool<TChain> Pool { get; } = pool;
    }

    public async Task<PoolRef<TChain>> AcquireAsync(PoolKey key, ConnectionOrigin origin, Func<CancellationToken, Task<TChain>> factory, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connectLocks.TryGetValue(key, out gate!)) _connectLocks[key] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(key, out var list))
                {
                    foreach (var e in list)
                    {
                        if (e.Refs > 0 && e.Chain.IsReusableFor(origin))
                        {
                            e.Refs++;
                            return new PoolRef<TChain>(e);
                        }
                    }
                }
            }

            var chain = await factory(ct);
            lock (_lock)
            {
                if (_disposed)
                {
                    _ = chain.DisposeAsync().AsTask();
                    throw new ObjectDisposedException(GetType().Name);
                }
                var entry = new Entry(this, key, chain) { Refs = 1 };
                if (!_entries.TryGetValue(key, out var list)) _entries[key] = list = [];
                list.Add(entry);
                return new PoolRef<TChain>(entry);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal PoolRef<TChain> AddRef(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Refs <= 0) throw new ObjectDisposedException(nameof(PoolRef<TChain>), "The connection has already been released.");
            entry.Refs++;
        }
        return new PoolRef<TChain>(entry);
    }

    internal ValueTask ReleaseAsync(Entry entry)
    {
        lock (_lock)
        {
            if (entry.Refs <= 0) return ValueTask.CompletedTask;
            if (--entry.Refs > 0) return ValueTask.CompletedTask;
            if (_entries.TryGetValue(entry.Key, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0) _entries.Remove(entry.Key);
            }
        }
        return entry.Chain.DisposeAsync();
    }

    /// <summary>Live references of all entries for <paramref name="key"/> (tests/diagnostics).</summary>
    public int RefCount(PoolKey key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var l) ? l.Sum(e => e.Refs) : 0;
    }

    public int EntryCount
    {
        get { lock (_lock) return _entries.Values.Sum(l => l.Count); }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = [.. _entries.Values.SelectMany(l => l)];
            foreach (var e in all) e.Refs = 0;
            _entries.Clear();
        }
        foreach (var e in all) await e.Chain.DisposeAsync();
    }
}

/// <summary>One reference on a pooled chain. Dispose exactly releases this reference (idempotent).</summary>
internal sealed class PoolRef<TChain> : IAsyncDisposable where TChain : class, IPooledChain
{
    readonly ConnectionPool<TChain>.Entry _entry;
    int _released;

    internal PoolRef(ConnectionPool<TChain>.Entry entry) => _entry = entry;

    public TChain Chain => _entry.Chain;
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>An additional, independently released reference (for channels opened from a lease).</summary>
    public PoolRef<TChain> AddRef()
    {
        ObjectDisposedException.ThrowIf(IsReleased, this);
        return _entry.Pool.AddRef(_entry);
    }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _released, 1) == 0 ? _entry.Pool.ReleaseAsync(_entry) : ValueTask.CompletedTask;
}
