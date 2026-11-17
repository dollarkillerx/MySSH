using System.Runtime.CompilerServices;

namespace MySSH.Storage;

/// <summary>
/// <c>await ThreadPoolHop.Instance</c> continues the method on the thread pool (a no-op when already there).
/// Microsoft.Data.Sqlite's async methods run synchronously, and the OS keystore blocks (the macOS Keychain may wait for
/// the user), so storage moves off the caller's thread itself: callers on the UI thread stay responsive, and their
/// awaits still resume on the UI thread.
/// </summary>
internal readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    public static ThreadPoolHop Instance => default;

    public ThreadPoolHop GetAwaiter() => this;

    public bool IsCompleted =>
        Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current is null && TaskScheduler.Current == TaskScheduler.Default;

    public void GetResult() { }

    public void OnCompleted(Action continuation) =>
        ThreadPool.QueueUserWorkItem(static c => c(), continuation, preferLocal: false);

    public void UnsafeOnCompleted(Action continuation) =>
        ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false);
}
