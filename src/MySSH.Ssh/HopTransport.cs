using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace MySSH.Ssh;

/// <summary>A local endpoint that reaches one hop through the previous one. Disposing it closes the tunnel.</summary>
public sealed class HopEndpoint : IDisposable
{
    readonly Action _close;
    int _disposed;

    public HopEndpoint(string host, int port, Action close)
    {
        Host = host;
        Port = port;
        _close = close;
    }

    public string Host { get; }
    public int Port { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _close();
    }
}

/// <summary>
/// How hop N+1 is reached through hop N (design §8). The endpoint must stay alive for the whole lifetime of the client that
/// uses it (stopping a ForwardedPortLocal early kills the session, P0 Q1e).
/// </summary>
public interface IHopTransport
{
    /// <param name="via">Connected client of the previous hop.</param>
    /// <param name="realHost">Real host name of the next hop as seen from <paramref name="via"/>.</param>
    /// <param name="onViolation">Called (on any thread) when someone other than our own client uses the tunnel: the chain is closed.</param>
    Task<HopEndpoint> OpenAsync(SshClient via, string realHost, int realPort, Action<string> onViolation, CancellationToken ct);
}

/// <summary>
/// Default: <c>ForwardedPortLocal("127.0.0.1", 0, host, port)</c>. The forward is reachable by every local process (P0 Q1d),
/// so the first accepted connection is ours and any further one closes the chain (fail-closed).
/// </summary>
public sealed class ForwardedPortHopTransport : IHopTransport
{
    public Task<HopEndpoint> OpenAsync(SshClient via, string realHost, int realPort, Action<string> onViolation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(via);
        ArgumentNullException.ThrowIfNull(onViolation);
        return Task.Run(() =>
        {
            var fwd = new ForwardedPortLocal("127.0.0.1", 0, realHost, checked((uint)realPort));
            int accepted = 0;
            fwd.RequestReceived += (_, e) =>
            {
                if (Interlocked.Increment(ref accepted) > 1)
                    onViolation($"Unexpected extra local connection ({e.OriginatorHost}:{e.OriginatorPort}) to the tunnel towards {realHost}:{realPort}; connection closed.");
            };
            via.AddForwardedPort(fwd);
            try
            {
                fwd.Start();
            }
            catch
            {
                try { via.RemoveForwardedPort(fwd); } catch (InvalidOperationException) { }
                fwd.Dispose();
                throw;
            }
            return new HopEndpoint("127.0.0.1", checked((int)fwd.BoundPort), () =>
            {
                try { fwd.Stop(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or SocketException) { }
                try { via.RemoveForwardedPort(fwd); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
                fwd.Dispose();
            });
        }, ct);
    }
}

/// <summary>
/// Hardened transport: a loopback listener that accepts exactly ONE connection, stops listening immediately and pumps the
/// socket through an SSH.NET-internal direct-tcpip channel (<see cref="SshNetInternals"/>, NativeAOT-safe accessors).
/// When the startup self-check fails (internals changed after an SSH.NET upgrade) it falls back to
/// <see cref="ForwardedPortHopTransport"/>. Remaining window: between Listen and our own connect (~1 ms) another local
/// process could win the accept; our connect then fails (fail-closed), it never reaches an authenticated session.
/// </summary>
public sealed class SingleAcceptBridgeTransport : IHopTransport
{
    readonly ForwardedPortHopTransport _fallback = new();

    /// <summary>Result of the startup self-check (cached).</summary>
    public static bool IsSupported => SshNetInternals.IsBridgeSupported;

    public Task<HopEndpoint> OpenAsync(SshClient via, string realHost, int realPort, Action<string> onViolation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(via);
        if (!IsSupported || SshNetInternals.TryGetSession(via) is not { } session)
            return _fallback.OpenAsync(via, realHost, realPort, onViolation, ct);
        var bridge = new Bridge(session, realHost, checked((uint)realPort));
        return Task.FromResult(new HopEndpoint("127.0.0.1", bridge.Port, bridge.Dispose));
    }

    sealed class Bridge : IForwardedPort
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly Lock _lock = new();
        Socket? _socket;
        bool _disposed;

        public event EventHandler? Closing;

        public int Port { get; }

        public Bridge(Session session, string host, uint port)
        {
            _listener.Start(1);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Factory.StartNew(() => Pump(session, host, port), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        void Pump(Session session, string host, uint port)
        {
            object? channel = null;
            try
            {
                var sock = _listener.AcceptSocket();
                lock (_lock)
                {
                    _socket = sock;
                    if (_disposed) { sock.Dispose(); return; }
                }
                _listener.Stop(); // nobody else can connect from now on
                channel = SshNetInternals.CreateDirectTcpip(session);
                SshNetInternals.DirectTcpipOpen(channel, host, port, this, sock);
                SshNetInternals.DirectTcpipBind(channel); // blocks: socket <-> channel until EOF/close
            }
#pragma warning disable CA1031 // background pump: any failure just closes the tunnel; the SSH client on top reports it
            catch (Exception)
            {
            }
#pragma warning restore CA1031
            finally
            {
                (channel as IDisposable)?.Dispose();
                lock (_lock) _socket?.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Closing?.Invoke(this, EventArgs.Empty);
            try { _listener.Stop(); } catch (SocketException) { }
            lock (_lock) _socket?.Dispose();
        }
    }
}
