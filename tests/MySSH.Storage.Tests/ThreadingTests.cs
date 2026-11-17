using MySSH.Core;

namespace MySSH.Storage.Tests;

/// <summary>
/// The desktop app calls storage from the UI thread. SQLite's async API is synchronous and the macOS Keychain may wait for
/// the user, so storage itself moves that work to the thread pool (ThreadPoolHop).
/// </summary>
public sealed class ThreadingTests
{
    /// <summary>Records where the keystore was read: on the pool with no synchronization context, or not.</summary>
    sealed class RecordingKeyStore : IOsKeyStore
    {
        readonly InMemoryKeyStore _inner = new();
        public List<bool> LoadsOnPool { get; } = [];
        public string Kind => _inner.Kind;
        public bool IsAvailable => true;

        public byte[]? Load()
        {
            LoadsOnPool.Add(Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current is null);
            return _inner.Load();
        }

        public void Save(ReadOnlySpan<byte> key) => _inner.Save(key);
        public void Delete() => _inner.Delete();
    }

    /// <summary>Runs <paramref name="work"/> on a dedicated (non-pool) thread with a synchronization context, like a UI thread.</summary>
    static TResult OnUiLikeThread<TResult>(Func<Task<TResult>> work)
    {
        TResult result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            try { result = work().GetAwaiter().GetResult(); }
            catch (Exception e) { error = e; }
        });
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public void ThreadPoolHop_leaves_a_ui_like_thread()
    {
        var (onPool, context) = OnUiLikeThread(async () =>
        {
            await ThreadPoolHop.Instance;
            return (Thread.CurrentThread.IsThreadPoolThread, SynchronizationContext.Current);
        });
        Assert.True(onPool);
        Assert.Null(context);
    }

    [Fact]
    public async Task Keystore_unlock_and_queries_do_not_run_on_the_callers_thread()
    {
        using var dir = new TempDir();
        var ks = new RecordingKeyStore();
        await using (var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct))
        {
            await s.Vault.InitializeWithOsKeystoreAsync(ct: T.Ct);
            await s.Hosts.SaveAsync(new Host { Label = "web", Hostname = "203.0.113.10" }, T.Ct);
        }
        ks.LoadsOnPool.Clear();

        var labels = OnUiLikeThread(async () =>
        {
            await using var s = await MySshStorage.OpenAsync(T.Options(dir, ks), T.Ct); // auto-unlock reads the keystore
            Assert.Equal(VaultState.Unlocked, s.Vault.State);
            return (await s.Hosts.ListAsync(T.Ct)).Select(h => h.Label).ToList();
        });

        Assert.Equal(["web"], labels);
        Assert.NotEmpty(ks.LoadsOnPool);
        Assert.All(ks.LoadsOnPool, Assert.True);
    }
}
