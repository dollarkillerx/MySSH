using System.Net;
using System.Net.Sockets;
using System.Text;
using MySSH.Core;

namespace MySSH.Ssh.IntegrationTests;

/// <summary>ILocalForward (used for RDP through SSH jump hosts) against the local sshd: bytes flow both ways.</summary>
[Collection(SshdGroup.Name)]
public sealed class LocalForwardTests(SshdEnvironment env)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A loopback echo server standing in for a Windows box's RDP port.</summary>
    static (TcpListener Listener, Task Loop) StartEcho()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var loop = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        int n;
                        try { while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n)); }
                        catch (IOException) { }
                    }
                });
            }
        });
        return (l, loop);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forward_reaches_a_tcp_service_behind_the_ssh_host(bool viaBastion)
    {
        env.RequireSshd();
        var (echo, _) = StartEcho();
        try
        {
            await using var lab = new Lab(env);
            var host = viaBastion ? lab.AddHost("target", jumps: [lab.AddHost("bastion")]) : lab.AddHost("target");
            await using var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
            var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
            await using var fwd = await conn.OpenLocalForwardAsync("127.0.0.1", echoPort, Ct);
            Assert.Equal("127.0.0.1", fwd.LocalHost);
            Assert.NotEqual(echoPort, fwd.LocalPort);

            for (var round = 0; round < 2; round++) // the RDP client may reconnect: every connection works
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, fwd.LocalPort, Ct);
                var s = c.GetStream();
                var msg = Encoding.UTF8.GetBytes($"rdp-bytes-{round}-远程");
                await s.WriteAsync(msg, Ct);
                var buf = new byte[msg.Length];
                await s.ReadExactlyAsync(buf, Ct);
                Assert.Equal(msg, buf);
            }
            Assert.Equal(2, fwd.ConnectionCount);

            var port = fwd.LocalPort;
            await fwd.DisposeAsync();
            using var after = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(async () => await after.ConnectAsync(IPAddress.Loopback, port, Ct));
        }
        finally { echo.Stop(); }
    }

    /// <summary>Review finding: a stop slower than the timeout threw from DisposeAsync and kept the SSH lease.</summary>
    [Fact(Timeout = 90_000)]
    public async Task Dispose_with_a_busy_tunnel_does_not_throw_and_releases_the_connection()
    {
        env.RequireSshd();
        var (echo, _) = StartEcho();
        var saved = SshLocalForward.StopTimeout;
        SshLocalForward.StopTimeout = TimeSpan.Zero; // every stop "times out"
        try
        {
            await using var lab = new Lab(env);
            var host = lab.AddHost("target");
            var connector = lab.Connector();
            var conn = await connector.ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
            var fwd = await conn.OpenLocalForwardAsync("127.0.0.1", ((IPEndPoint)echo.LocalEndpoint).Port, Ct);
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, fwd.LocalPort, Ct); // an open RDP connection keeps Stop() busy
            await c.GetStream().WriteAsync("x"u8.ToArray(), Ct);

            await fwd.DisposeAsync(); // must not throw TimeoutException
            await conn.DisposeAsync();
            // Both leases gone: the pooled chain closes even though the tunnel stop is still running.
            Assert.Equal(0, connector.PooledConnectionCount);
            Assert.Equal(SshConnectionState.Closed, conn.State);
        }
        finally
        {
            SshLocalForward.StopTimeout = saved;
            echo.Stop();
        }
    }
}
