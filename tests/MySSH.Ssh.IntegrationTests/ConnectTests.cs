using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MySSH.Core;
using MySSH.Ssh.Tests;
using Renci.SshNet;

namespace MySSH.Ssh.IntegrationTests;

[Collection(SshdGroup.Name)]
public sealed class ConnectTests(SshdEnvironment env)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>sshd DEBUG1 line proving a direct-tcpip tunnel to 127.0.0.1:port went through that hop.</summary>
    static string Tunnel(int port) => $@"server_request_direct_tcpip: originator \S+ port \d+, target 127\.0\.0\.1 port {port}\b";

    [Fact(Timeout = 90_000)]
    public async Task Direct_key_auth_runs_a_command()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var host = lab.AddHost("target");
        await using var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        Assert.Equal(SshConnectionState.Connected, conn.State);
        var r = await conn.ExecAsync("echo hello", ct: Ct);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("hello", r.Stdout.Trim());
        // TOFU stored the REAL host:port
        var kh = Assert.Single(await lab.Known.ListAsync(Ct));
        Assert.Equal($"[127.0.0.1]:{env["target"].Port}", kh.HostPort);
        Assert.StartsWith("SHA256:", kh.Sha256Fingerprint, StringComparison.Ordinal);
        var (code, keygen) = await SshdEnvironment.RunAsync("/usr/bin/ssh-keygen", ["-lf", Path.Combine(env["target"].Dir, "ssh_host_ed25519_key.pub")]);
        Assert.Equal(0, code);
        Assert.Contains(kh.Sha256Fingerprint, keygen, StringComparison.Ordinal);
    }

    [Fact(Timeout = 90_000)]
    public async Task Encrypted_key_prompts_until_the_passphrase_is_right()
    {
        env.RequireSshd();
        await using var lab = new Lab(env, HostKeyDecision.AcceptAndSave, "wrong", SshdEnvironment.Passphrase);
        var host = lab.AddHost("target", lab.Encrypted);
        await using var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        Assert.Equal(0, (await conn.ExecAsync("true", ct: Ct)).ExitCode);
        Assert.Equal(2, lab.Prompt.Errors.Count);
        Assert.Null(lab.Prompt.Errors[0]);
        Assert.Equal(Credentials.WrongPassphraseMessage, lab.Prompt.Errors[1]);
    }

    [Fact(Timeout = 90_000)]
    public async Task Encrypted_pem_key_with_stored_passphrase_connects_without_prompt()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var pem = lab.Ids.Add(new Identity { Label = "pem", Username = env.User, Kind = IdentityKind.PrivateKey, HasPassphrase = true },
            new IdentitySecrets(null, env.KeyRsaPemEncrypted, SshdEnvironment.Passphrase));
        var host = lab.AddHost("target", pem);
        await using var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        Assert.Equal(0, (await conn.ExecAsync("true", ct: Ct)).ExitCode);
        Assert.Empty(lab.Prompt.Errors);
    }

    [Fact(Timeout = 90_000)]
    public async Task Cancelled_passphrase_prompt_and_ai_origin_fail_clearly()
    {
        env.RequireSshd();
        await using var lab = new Lab(env); // prompt answers null = cancel
        var host = lab.AddHost("target", lab.Encrypted);
        var c = lab.Connector();
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => c.ConnectAsync(host.Id, ConnectionOrigin.User, Ct));
        Assert.Equal(SshFailureKind.PassphraseCancelled, ex.Kind);
        ex = await Assert.ThrowsAsync<SshConnectException>(() => c.ConnectAsync(host.Id, ConnectionOrigin.Ai, Ct));
        Assert.Equal(SshFailureKind.PassphraseRequired, ex.Kind);
        Assert.Single(lab.Prompt.Errors); // AI never prompts
    }

    [Fact(Timeout = 90_000)]
    public async Task Two_hop_jump_chain_goes_through_every_hop_and_stores_real_host_ports()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var bastion = lab.AddHost("bastion");
        var hop2 = lab.AddHost("hop2");
        var target = lab.AddHost("target", jumps: [bastion, hop2]);
        long hop2LogBefore = env["hop2"].ReadLog().Length;
        await using var conn = await lab.Connector().ConnectAsync(target.Id, ConnectionOrigin.User, Ct);
        var r = await conn.ExecAsync("echo $SSH_CONNECTION", ct: Ct);
        Assert.EndsWith($" {env["target"].Port}", r.Stdout.Trim(), StringComparison.Ordinal);
        await env["hop2"].AssertLogAsync(hop2LogBefore, Tunnel(env["target"].Port));

        var stored = (await lab.Known.ListAsync(Ct)).Select(k => k.HostPort).Order().ToArray();
        string[] expected = [.. new[] { "bastion", "hop2", "target" }.Select(n => $"[127.0.0.1]:{env[n].Port}").Order()];
        Assert.Equal(expected, stored);
        var checks = lab.Verifier.Checks.ToArray();
        Assert.Equal([0, 1, 2], checks.Select(c => c.HopIndex));
        Assert.All(checks, c => Assert.Equal(3, c.HopCount));
        Assert.All(checks, c => Assert.Equal(HostKeyStatus.Unknown, c.Status));
    }

    [Fact(Timeout = 90_000)]
    public async Task Socks5_proxy_alone_and_in_front_of_a_jump_chain()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var socks = lab.Proxies.Add(new ProxyProfile { Label = "socks", Kind = ProxyKind.Socks5, Hostname = "127.0.0.1", Port = env.SocksPort });
        var viaProxy = lab.AddHost("target", proxy: socks);
        var connector = lab.Connector();

        long bastionLog = env["bastion"].ReadLog().Length;
        await using (var conn = await connector.ConnectAsync(viaProxy.Id, ConnectionOrigin.User, Ct))
            Assert.Equal("ok", (await conn.ExecAsync("echo ok", ct: Ct)).Stdout.Trim());
        await env["bastion"].AssertLogAsync(bastionLog, Tunnel(env["target"].Port));

        // proxy → hop2 → target: the proxy applies to the first hop only
        var hop2 = lab.AddHost("hop2");
        var chained = lab.Hosts.Add(new Host { Label = "target-via-proxy-and-hop2", Hostname = "127.0.0.1", Port = env["target"].Port, IdentityId = lab.Plain.Id, JumpChain = [hop2.Id], ProxyId = socks.Id });
        bastionLog = env["bastion"].ReadLog().Length;
        long hop2Log = env["hop2"].ReadLog().Length;
        await using (var conn = await connector.ConnectAsync(chained.Id, ConnectionOrigin.User, Ct))
            Assert.Equal("ok", (await conn.ExecAsync("echo ok", ct: Ct)).Stdout.Trim());
        await env["bastion"].AssertLogAsync(bastionLog, Tunnel(env["hop2"].Port));
        await env["hop2"].AssertLogAsync(hop2Log, Tunnel(env["target"].Port));
    }

    [Fact(Timeout = 90_000)]
    public async Task Proxy_that_is_down_reports_a_proxy_error()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        int deadPort;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); deadPort = ((IPEndPoint)l.LocalEndpoint).Port; }
        var dead = lab.Proxies.Add(new ProxyProfile { Label = "dead", Kind = ProxyKind.Socks5, Hostname = "127.0.0.1", Port = deadPort });
        var host = lab.AddHost("target", proxy: dead);
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct));
        Assert.Equal(SshFailureKind.Proxy, ex.Kind);
        Assert.Equal(0, ex.HopIndex);
    }

    [Fact(Timeout = 90_000)]
    public async Task Tofu_unknown_accept_and_save_then_silent_then_changed_and_rejected()
    {
        env.RequireSshd();
        await env.StartInstanceAsync("rotating");
        await using var lab = new Lab(env);
        var host = lab.AddHost("rotating");
        var hostPort = $"[127.0.0.1]:{env["rotating"].Port}";

        await using (var c = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct))
            Assert.Equal(0, (await c.ExecAsync("true", ct: Ct)).ExitCode);
        var first = Assert.Single(lab.Verifier.Checks);
        Assert.Equal(HostKeyStatus.Unknown, first.Status);
        Assert.Equal(hostPort, first.HostPort);
        Assert.Equal((0, 1), (first.HopIndex, first.HopCount));
        Assert.Equal(1, lab.Known.Upserts);

        await using (var c = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct))
            Assert.Equal(SshConnectionState.Connected, c.State);
        Assert.Single(lab.Verifier.Checks); // second connect is silent

        await env.RegenerateHostKeyAsync("rotating");
        lab.Verifier = new ScriptedVerifier(HostKeyDecision.Reject);
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct));
        Assert.Equal(SshFailureKind.HostKeyRejected, ex.Kind);
        var changed = Assert.Single(lab.Verifier.Checks);
        Assert.Equal(HostKeyStatus.Changed, changed.Status);
        Assert.Equal(first.Fingerprint, changed.PreviousFingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.Equal(first.Fingerprint, Assert.Single(await lab.Known.FindAsync(hostPort, Ct)).Sha256Fingerprint); // store untouched

        // AcceptOnce connects but does not persist
        lab.Verifier = new ScriptedVerifier(HostKeyDecision.AcceptOnce);
        await using (var c = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct))
            Assert.Equal(SshConnectionState.Connected, c.State);
        Assert.Equal(first.Fingerprint, Assert.Single(await lab.Known.FindAsync(hostPort, Ct)).Sha256Fingerprint);
        await env.KillInstanceAsync("rotating");
    }

    [Fact(Timeout = 90_000)]
    public async Task Ai_origin_is_strict_never_prompts_and_uses_the_ai_identity()
    {
        env.RequireSshd();
        await env.StartInstanceAsync("rotating");
        await using var lab = new Lab(env, HostKeyDecision.AcceptAndSave, SshdEnvironment.Passphrase);
        // User identity needs a prompt; the AI identity is a different (unencrypted) key.
        var host = lab.AddHost("rotating", lab.Encrypted, aiIdentity: lab.Plain.Id);
        var connector = lab.Connector();

        var ex = await Assert.ThrowsAsync<SshConnectException>(() => connector.ConnectAsync(host.Id, ConnectionOrigin.Mcp, Ct));
        Assert.Equal(SshFailureKind.HostKeyUnknown, ex.Kind);
        Assert.Empty(lab.Verifier.Checks);

        await using var user = await connector.ConnectAsync(host.Id, ConnectionOrigin.User, Ct); // TOFU + passphrase prompt
        Assert.Single(lab.Prompt.Errors);
        await using (var ai = await connector.ConnectAsync(host.Id, ConnectionOrigin.Ai, Ct))
        {
            Assert.Equal(0, (await ai.ExecAsync("true", ct: Ct)).ExitCode);
            Assert.Equal(2, connector.PooledConnectionCount); // different identity → separate chain
        }
        Assert.Single(lab.Prompt.Errors);
        Assert.Single(lab.Verifier.Checks);

        await env.RegenerateHostKeyAsync("rotating");
        ex = await Assert.ThrowsAsync<SshConnectException>(() => lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.Ai, Ct));
        Assert.Equal(SshFailureKind.HostKeyChanged, ex.Kind);
        Assert.Single(lab.Verifier.Checks);
        await env.KillInstanceAsync("rotating");
    }

    [Fact(Timeout = 90_000)]
    public async Task Pool_shares_one_chain_and_channels_keep_it_alive()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var bastion = lab.AddHost("bastion");
        var host = lab.AddHost("target", jumps: [bastion]);
        var connector = lab.Connector();
        var a = await connector.ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        var b = await connector.ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        Assert.Equal(1, connector.PooledConnectionCount);
        await a.DisposeAsync();
        await a.DisposeAsync(); // idempotent
        Assert.Equal(SshConnectionState.Connected, b.State);
        var sftp = await b.OpenSftpAsync(Ct);
        await b.DisposeAsync();
        Assert.Equal(1, connector.PooledConnectionCount);
        Assert.NotEmpty(await sftp.ListAsync(sftp.HomeDirectory, Ct));
        var states = new List<SshConnectionState>();
        await sftp.DisposeAsync();
        Assert.Equal(0, connector.PooledConnectionCount);
        Assert.Equal(SshConnectionState.Closed, a.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => b.ExecAsync("true", ct: Ct));
    }

    sealed class CapturingTransport(IHopTransport inner) : IHopTransport
    {
        public readonly List<HopEndpoint> Endpoints = [];
        public async Task<HopEndpoint> OpenAsync(SshClient via, string realHost, int realPort, Action<string> onViolation, CancellationToken ct)
        {
            var ep = await inner.OpenAsync(via, realHost, realPort, onViolation, ct);
            lock (Endpoints) Endpoints.Add(ep);
            return ep;
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Forwarded_port_fails_closed_on_a_second_local_connection()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var bastion = lab.AddHost("bastion");
        var host = lab.AddHost("target", jumps: [bastion]);
        var transport = new CapturingTransport(new ForwardedPortHopTransport());
        await using var conn = await lab.Connector(new SshConnectorOptions { CustomHopTransport = transport, HeartbeatInterval = TimeSpan.Zero })
            .ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        var lost = new TaskCompletionSource<SshConnectionStateChange>();
        conn.StateChanged += s => { if (s.Current == SshConnectionState.Lost) lost.TrySetResult(s); };
        using var intruder = new TcpClient();
        await intruder.ConnectAsync(IPAddress.Loopback, transport.Endpoints.Last().Port, Ct);
        var change = await lost.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("Unexpected extra local connection", change.Reason, StringComparison.Ordinal);
        Assert.Equal(SshConnectionState.Lost, conn.State);
        await Assert.ThrowsAsync<SshConnectionLostException>(() => conn.ExecAsync("true", ct: Ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task Single_accept_bridge_passes_self_check_and_refuses_a_second_connection()
    {
        env.RequireSshd();
        Assert.True(SingleAcceptBridgeTransport.IsSupported);
        await using var lab = new Lab(env);
        var bastion = lab.AddHost("bastion");
        var hop2 = lab.AddHost("hop2");
        var host = lab.AddHost("target", jumps: [bastion, hop2]);
        var transport = new CapturingTransport(new SingleAcceptBridgeTransport());
        await using var conn = await lab.Connector(new SshConnectorOptions { CustomHopTransport = transport, HeartbeatInterval = TimeSpan.Zero })
            .ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        Assert.Equal("ok", (await conn.ExecAsync("echo ok", ct: Ct)).Stdout.Trim());
        using var intruder = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => intruder.ConnectAsync(IPAddress.Loopback, transport.Endpoints.Last().Port, Ct).AsTask());
        Assert.Equal(SshConnectionState.Connected, conn.State);
        await using var sftp = await conn.OpenSftpAsync(Ct); // a second target session through the bridge transport
        Assert.False(string.IsNullOrEmpty(sftp.HomeDirectory));
    }

    [Fact(Timeout = 90_000)]
    public async Task Connection_is_lost_when_the_bastion_dies()
    {
        env.RequireSshd();
        await env.StartInstanceAsync("doomed");
        await using var lab = new Lab(env);
        var doomed = lab.AddHost("doomed");
        var host = lab.AddHost("target", jumps: [doomed]);
        await using var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        await using var shell = await conn.OpenShellAsync(80, 24, ct: Ct);
        var exited = new TaskCompletionSource<TerminalExit>();
        shell.Exited += e => exited.TrySetResult(e);
        var lost = new TaskCompletionSource<SshConnectionStateChange>();
        conn.StateChanged += s => { if (s.Current == SshConnectionState.Lost) lost.TrySetResult(s); };

        await env.KillInstanceAsync("doomed");
        var change = await lost.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Contains("doomed", change.Reason, StringComparison.Ordinal);
        var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(exit.ConnectionLost);
        Assert.Null(exit.ExitCode);
        await shell.WriteAsync("echo after\n"u8.ToArray(), Ct); // no-op, must not throw
    }

    [Fact(Timeout = 90_000)]
    public async Task Heartbeat_detects_a_frozen_peer()
    {
        env.RequireSshd();
        await env.StartInstanceAsync("frozen");
        await using var lab = new Lab(env);
        var host = lab.AddHost("frozen");
        await using var conn = await lab.Connector(new SshConnectorOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            HeartbeatTimeout = TimeSpan.FromSeconds(1),
            OperationTimeout = TimeSpan.FromSeconds(3),
        }).ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        var lost = new TaskCompletionSource<SshConnectionStateChange>();
        conn.StateChanged += s => { if (s.Current == SshConnectionState.Lost) lost.TrySetResult(s); };
        await Task.Delay(2500, Ct); // a few successful heartbeats first
        Assert.Equal(SshConnectionState.Connected, conn.State);
        try
        {
            await env.KillInstanceAsync("frozen", "STOP");
            var change = await lost.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Contains("heartbeat", change.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await env.KillInstanceAsync("frozen", "CONT");
            await env.KillInstanceAsync("frozen");
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Missing_jump_host_and_cycles_fail_before_connecting()
    {
        env.RequireSshd();
        await using var lab = new Lab(env);
        var a = lab.AddHost("bastion");
        var b = lab.Hosts.Add(new Host { Label = "b", Hostname = "127.0.0.1", Port = env["hop2"].Port, IdentityId = lab.Plain.Id, JumpChain = [a.Id] });
        lab.Hosts.Add(a with { JumpChain = [b.Id] });
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => lab.Connector().ConnectAsync(b.Id, ConnectionOrigin.User, Ct));
        Assert.Equal(SshFailureKind.InvalidConfiguration, ex.Kind);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        var c = lab.Hosts.Add(new Host { Label = "c", Hostname = "127.0.0.1", Port = 1, IdentityId = lab.Plain.Id, JumpChain = [Guid.NewGuid()] });
        ex = await Assert.ThrowsAsync<SshConnectException>(() => lab.Connector().ConnectAsync(c.Id, ConnectionOrigin.User, Ct));
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Empty(lab.Verifier.Checks);
    }
}
