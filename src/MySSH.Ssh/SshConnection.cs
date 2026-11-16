using MySSH.Core;

namespace MySSH.Ssh;

public enum SshConnectionState { Connecting = 0, Connected = 1, Lost = 2, Closed = 3 }

/// <param name="Reason">Why the connection was lost (hop label + message), null for normal transitions.</param>
public sealed record SshConnectionStateChange(SshConnectionState Previous, SshConnectionState Current, string? Reason);

/// <summary>
/// A lease on a pooled SSH chain (design §8). Disposing releases this lease only; shells and SFTP sessions opened from it
/// hold their own reference, so the chain is closed (in reverse hop order) when the last of them is released.
/// </summary>
public interface ISshConnection : IAsyncDisposable
{
    Host Host { get; }
    ConnectionOrigin Origin { get; }
    SshConnectionState State { get; }

    /// <summary>Raised on a background thread. Lost is raised when ANY hop reports an error, a tunnel is misused, or the heartbeat fails.</summary>
    event Action<SshConnectionStateChange>? StateChanged;

    /// <summary>Opens an interactive PTY shell. The returned backend keeps the connection alive until disposed.</summary>
    Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default);

    /// <summary>Runs a command with bounded output. Cancellation and timeout kill the remote process.</summary>
    Task<ExecResult> ExecAsync(string command, ExecOptions? options = null, CancellationToken ct = default);

    /// <summary>Opens an SFTP session (its own SSH session to the target through the same chain, same pinned host key).</summary>
    Task<ISftpSession> OpenSftpAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens 127.0.0.1:&lt;random&gt; → <paramref name="remoteHost"/>:<paramref name="remotePort"/> as seen from the target
    /// (e.g. RDP to a Windows host behind this SSH host). Keeps the connection alive until the forward is disposed.
    /// </summary>
    Task<ILocalForward> OpenLocalForwardAsync(string remoteHost, int remotePort, CancellationToken ct = default) =>
        throw new NotSupportedException("This connection does not support port forwarding.");
}

internal sealed class SshConnectionLease : ISshConnection
{
    readonly PoolRef<SshChain> _ref;
    readonly Lock _lock = new();
    Action<SshConnectionStateChange>? _handlers;
    int _disposed;

    public SshConnectionLease(PoolRef<SshChain> poolRef, ConnectionOrigin origin)
    {
        _ref = poolRef;
        Origin = origin;
        poolRef.Chain.StateChanged += Forward;
    }

    SshChain Chain => _ref.Chain;
    public Host Host => Chain.Host;
    public ConnectionOrigin Origin { get; }
    public SshConnectionState State => Chain.State;

    public event Action<SshConnectionStateChange>? StateChanged
    {
        add { lock (_lock) _handlers += value; }
        remove { lock (_lock) _handlers -= value; }
    }

    void Forward(SshConnectionStateChange change)
    {
        Action<SshConnectionStateChange>? h;
        lock (_lock) h = _handlers;
        h?.Invoke(change);
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Chain.ThrowIfNotConnected();
        var r = _ref.AddRef();
        try
        {
            return await SshShellBackend.OpenAsync(r, columns, rows, term, ct);
        }
        catch
        {
            await r.DisposeAsync();
            throw;
        }
    }

    public async Task<ExecResult> ExecAsync(string command, ExecOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Chain.ThrowIfNotConnected();
        var r = _ref.AddRef();
        await using (r)
            return await SshExec.RunAsync(r.Chain, command, options ?? new ExecOptions(), ct);
    }

    public async Task<ISftpSession> OpenSftpAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Chain.ThrowIfNotConnected();
        var r = _ref.AddRef();
        try
        {
            return await SshSftpSession.OpenAsync(r, ct);
        }
        catch
        {
            await r.DisposeAsync();
            throw;
        }
    }

    public async Task<ILocalForward> OpenLocalForwardAsync(string remoteHost, int remotePort, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Chain.ThrowIfNotConnected();
        var r = _ref.AddRef();
        try
        {
            return await SshLocalForward.OpenAsync(r, remoteHost, remotePort, ct);
        }
        catch
        {
            await r.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        Chain.StateChanged -= Forward;
        return _ref.DisposeAsync();
    }
}
