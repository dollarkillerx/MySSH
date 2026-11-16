using System.Buffers.Binary;
using System.Threading.Channels;
using MySSH.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Connection;

namespace MySSH.Ssh;

/// <summary>
/// PTY shell over SSH.NET <see cref="ShellStream"/>. A dedicated reader thread always drains the stream (SSH.NET has no
/// back-pressure, P0 Q5c); input and window changes go through one ordered queue on a background task so callers (the UI
/// thread) never block on the channel window. Exit status: SSH.NET has no API for it, so the public <see cref="Session"/>
/// (reached through an AOT-safe accessor, <see cref="SshNetInternals"/>) is watched for <c>exit-status</c> requests; the shell's
/// channel number is the CHANNEL_OPEN_CONFIRMATION received while opening it (all channel opens on the target session go
/// through <see cref="SshChain.ChannelOpenGate"/>, so it is unambiguous). Null when unavailable.
/// </summary>
internal sealed class SshShellBackend : ITerminalBackend
{
    abstract record Op;
    sealed record WriteOp(byte[] Data) : Op;
    sealed record ResizeOp(uint Columns, uint Rows) : Op;

    readonly PoolRef<SshChain> _ref;
    readonly ShellStream _stream;
    readonly ExitStatusWatcher? _watcher;
    readonly Channel<Op> _ops = Channel.CreateUnbounded<Op>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource _cts = new();
    Thread? _reader;
    Task? _writer;
    int _exited;
    int _disposed;

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<TerminalExit>? Exited;

    SshShellBackend(PoolRef<SshChain> poolRef, ShellStream stream, ExitStatusWatcher? watcher)
    {
        _ref = poolRef;
        _stream = stream;
        _watcher = watcher;
    }

    public static async Task<ITerminalBackend> OpenAsync(PoolRef<SshChain> poolRef, int columns, int rows, string term, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        var chain = poolRef.Chain;
        var target = chain.Target;
        var session = SshNetInternals.IsExitStatusSupported ? SshNetInternals.TryGetSession(target) : null;
        var watcher = session is null ? null : new ExitStatusWatcher(session);
        ShellStream stream;
        await chain.ChannelOpenGate.WaitAsync(ct);
        try
        {
            watcher?.BeginOpen();
            // CreateShellStream opens the channel and sends pty-req + shell synchronously.
            stream = await Task.Run(() => target.CreateShellStream(term, (uint)columns, (uint)rows, 0, 0, 64 * 1024), ct);
        }
        catch
        {
            watcher?.Dispose();
            throw;
        }
        finally
        {
            watcher?.EndOpen();
            chain.ChannelOpenGate.Release();
        }
        var backend = new SshShellBackend(poolRef, stream, watcher);
        backend.Start();
        chain.MarkActivity();
        return backend;
    }

    void Start()
    {
        _ref.Chain.StateChanged += OnChainState;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ssh-shell-{_ref.Chain.Host.Label}" };
        _reader.Start();
        _writer = Task.Run(WriteLoopAsync);
        if (_ref.Chain.State is SshConnectionState.Lost or SshConnectionState.Closed) RaiseExit(new TerminalExit(null, true, _ref.Chain.Reason));
    }

    void OnChainState(SshConnectionStateChange change)
    {
        if (change.Current is SshConnectionState.Lost or SshConnectionState.Closed)
            RaiseExit(new TerminalExit(null, true, change.Reason ?? "Connection closed."));
    }

    void ReadLoop()
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                int n = _stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                _ref.Chain.MarkActivity();
                if (Volatile.Read(ref _exited) != 0) continue; // keep draining, nobody listens any more
                try { DataReceived?.Invoke(buffer.AsMemory(0, n)); }
#pragma warning disable CA1031 // a throwing subscriber must not stop draining the channel
                catch (Exception) { }
#pragma warning restore CA1031
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or InvalidOperationException)
        {
        }

        if (Volatile.Read(ref _disposed) != 0) return;
        var code = _watcher?.ExitCode;
        if (code is null)
        {
            // EOF without exit-status: give a concurrent ErrorOccurred a moment to classify it as connection loss.
            Thread.Sleep(100);
            code = _watcher?.ExitCode;
        }
        _watcher?.Dispose();
        var chain = _ref.Chain;
        bool lost = code is null && (chain.State != SshConnectionState.Connected || !SafeIsConnected(chain));
        RaiseExit(new TerminalExit(code, lost, lost ? chain.Reason ?? "Connection lost." : null));
    }

    static bool SafeIsConnected(SshChain chain)
    {
        try { return chain.Target.IsConnected; }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return false; }
    }

    void RaiseExit(TerminalExit exit)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        _ops.Writer.TryComplete();
        if (Volatile.Read(ref _disposed) != 0) return;
        try { Exited?.Invoke(exit); }
#pragma warning disable CA1031
        catch (Exception) { }
#pragma warning restore CA1031
    }

    async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var op in _ops.Reader.ReadAllAsync(_cts.Token))
            {
                switch (op)
                {
                    case WriteOp w:
                        _stream.Write(w.Data, 0, w.Data.Length);
                        _stream.Flush();
                        break;
                    case ResizeOp r:
                        _stream.ChangeWindowSize(r.Columns, r.Rows, 0, 0);
                        break;
                }
                _ref.Chain.MarkActivity();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException or IOException or InvalidOperationException)
        {
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (data.IsEmpty || Volatile.Read(ref _exited) != 0 || Volatile.Read(ref _disposed) != 0) return ValueTask.CompletedTask;
        _ops.Writer.TryWrite(new WriteOp(data.ToArray()));
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || Volatile.Read(ref _exited) != 0) return;
        _ops.Writer.TryWrite(new ResizeOp((uint)columns, (uint)rows));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ref.Chain.StateChanged -= OnChainState;
        _watcher?.Dispose();
        _ops.Writer.TryComplete();
        await _cts.CancelAsync();
        if (_writer is not null) await _writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.Run(() =>
        {
            try { _stream.Dispose(); }
            catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or InvalidOperationException) { }
            _reader?.Join(TimeSpan.FromSeconds(2));
        });
        _cts.Dispose();
        await _ref.DisposeAsync();
    }
}

/// <summary>Captures the shell's channel number (CHANNEL_OPEN_CONFIRMATION during the gated open) and its exit-status.</summary>
internal sealed class ExitStatusWatcher : IDisposable
{
    readonly Session _session;
    readonly Lock _lock = new();
    readonly Dictionary<uint, int> _exitByChannel = [];
    bool _opening;
    uint? _channel;
    int _disposed;

    public ExitStatusWatcher(Session session)
    {
        _session = session;
        session.ChannelOpenConfirmationReceived += OnOpenConfirmation;
        session.ChannelRequestReceived += OnRequest;
    }

    public void BeginOpen() { lock (_lock) _opening = true; }

    public void EndOpen()
    {
        lock (_lock) _opening = false;
        _session.ChannelOpenConfirmationReceived -= OnOpenConfirmation;
    }

    public uint? Channel { get { lock (_lock) return _channel; } }

    public int? ExitCode
    {
        get
        {
            lock (_lock) return _channel is uint ch && _exitByChannel.TryGetValue(ch, out var code) ? code : null;
        }
    }

    void OnOpenConfirmation(object? sender, MessageEventArgs<ChannelOpenConfirmationMessage> e)
    {
        lock (_lock) if (_opening) _channel ??= e.Message.LocalChannelNumber;
    }

    void OnRequest(object? sender, MessageEventArgs<ChannelRequestMessage> e)
    {
        var m = e.Message;
        if (m.RequestName != "exit-status" || m.RequestData is not { Length: >= 4 } d) return;
        lock (_lock)
        {
            // Before the channel number is known keep every exit-status; afterwards only ours.
            if (_channel is uint ch && ch != m.LocalChannelNumber) return;
            _exitByChannel[m.LocalChannelNumber] = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(d.Length - 4));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.ChannelOpenConfirmationReceived -= OnOpenConfirmation;
        _session.ChannelRequestReceived -= OnRequest;
    }
}
