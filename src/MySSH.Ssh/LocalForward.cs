using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySSH.Ssh;

/// <summary>
/// A loopback port that reaches <see cref="RemoteHost"/>:<see cref="RemotePort"/> from the SSH target (direct-tcpip), e.g.
/// a Windows box's RDP port behind a bastion. Any local process may connect while it is open (loopback only); it keeps the
/// chain alive until disposed.
/// </summary>
public interface ILocalForward : IAsyncDisposable
{
    /// <summary>Always 127.0.0.1.</summary>
    string LocalHost { get; }
    int LocalPort { get; }
    string RemoteHost { get; }
    int RemotePort { get; }
    /// <summary>Connections accepted so far (for the UI).</summary>
    int ConnectionCount { get; }
}

internal sealed class SshLocalForward : ILocalForward
{
    readonly PoolRef<SshChain> _ref;
    readonly SshClient _client;
    readonly ForwardedPortLocal _port;
    int _connections;
    int _disposed;

    SshLocalForward(PoolRef<SshChain> poolRef, SshClient client, ForwardedPortLocal port, string remoteHost, int remotePort)
    {
        _ref = poolRef;
        _client = client;
        _port = port;
        RemoteHost = remoteHost;
        RemotePort = remotePort;
        _port.RequestReceived += (_, _) => Interlocked.Increment(ref _connections);
    }

    public string LocalHost => "127.0.0.1";
    public int LocalPort => checked((int)_port.BoundPort);
    public string RemoteHost { get; }
    public int RemotePort { get; }
    public int ConnectionCount => Volatile.Read(ref _connections);

    public static Task<ILocalForward> OpenAsync(PoolRef<SshChain> poolRef, string remoteHost, int remotePort, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteHost);
        ArgumentOutOfRangeException.ThrowIfLessThan(remotePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
        return Task.Run<ILocalForward>(() =>
        {
            var client = poolRef.Chain.Target;
            var port = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, checked((uint)remotePort));
            try
            {
                client.AddForwardedPort(port);
                port.Start();
            }
            catch (Exception e)
            {
                try { client.RemoveForwardedPort(port); } catch (Exception r) when (r is ObjectDisposedException or InvalidOperationException) { }
                port.Dispose();
                // Callers only know the MySSH.Ssh error model, not SSH.NET's / socket exceptions.
                if (e is SocketException) throw new IOException($"Could not open a local port for the tunnel: {e.Message}", e);
                if (e is SshException or ObjectDisposedException) throw new SshConnectionLostException($"The SSH connection closed while opening the tunnel: {e.Message}", e);
                throw;
            }
            return new SshLocalForward(poolRef, client, port, remoteHost, remotePort);
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Stop() waits for open channels; bounded so a frozen peer never hangs the caller. After the timeout the stop
        // finishes in the background (it only touches this port) and the connection lease is released regardless.
        var stop = Task.Run(() =>
        {
            try { _port.Stop(); } catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or SocketException or SshException) { }
            try { _client.RemoveForwardedPort(_port); } catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { }
            try { _port.Dispose(); } catch (Exception e) when (e is ObjectDisposedException or SocketException or SshException) { }
        });
        try
        {
            await stop.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            await _ref.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Longest <see cref="DisposeAsync"/> waits for open tunnel connections to close.</summary>
    internal static TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
