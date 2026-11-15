using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshChainSpike;
using static SshChainSpike.Env;

// Usage: dotnet run [-- filter]   e.g. `dotnet run -- Q5`  (env.sh start must be running)
var filter = args.Length > 0 ? args[0] : "";
bool On(string id) => filter == "" || id.StartsWith(filter, StringComparison.OrdinalIgnoreCase) || filter.StartsWith(id, StringComparison.OrdinalIgnoreCase);
Results.Filter = filter;

Hop H(string name, int port, string key = "id_ed25519", string? pass = null)
    => new(name, Loopback, port, User, () => Auth.Key(User, key, pass));
var bastion = H("bastion", Bastion);
var target = H("target", Target);
var hop2 = H("hop2", Hop2);
SshClient MkSsh(ConnectionInfo ci) => new(ci);
TofuStore NewTofu(string name) => new(Path.Combine(Work, name + "_known_hosts"));

Console.WriteLine($"SSH.NET {typeof(SshClient).Assembly.GetName().Version}  user={User}  work={Work}");

// warm-up so the baseline for leak checks includes one-time static init
{
    using var c = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
    c.HostKeyReceived += (_, e) => e.CanTrust = true;
    await c.ConnectAsync(default); c.RunCommand("true"); c.Disconnect();
}
await Task.Delay(300); GC.Collect(); GC.WaitForPendingFinalizers();
int fd0 = OpenFdCount(), th0 = ThreadCount();
Console.WriteLine($"baseline fds={fd0} threads={th0}");

// =====================================================================================================
// Q1 jump chain via ForwardedPortLocal
// =====================================================================================================
if (On("Q1"))
{
    await Results.Run("Q1a", "1-hop jump: ForwardedPortLocal(127.0.0.1, 0) -> target", async () =>
    {
        var off = LogSize("bastion");
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, NewTofu("q1a"), TofuPolicy.AcceptNew, MkSsh);
        var fwd = chain.Forwards[0];
        var r = chain.Target.RunCommand("echo SSH_CONNECTION=$SSH_CONNECTION");
        await Task.Delay(200);
        var log = LogSince("bastion", off);
        var m = Regex.Match(log, @"server_request_direct_tcpip: originator (\S+) port (\d+), target (\S+) port (\d+)");
        bool ok = fwd.BoundPort > 0 && fwd.BoundHost == Loopback && r.ExitStatus == 0 && m.Success && m.Groups[4].Value == "2223";
        return Results.Ok(ok,
            $"BoundHost={fwd.BoundHost} BoundPort={fwd.BoundPort} (requested 0)\n" +
            $"target says: {r.Result.Trim()}\n" +
            $"bastion sshd log: {(m.Success ? m.Value : "<no direct-tcpip line>")}\n" +
            $"timings: {string.Join("; ", chain.Timings.Select(t => $"{t.hop}@{t.endpoint} {t.ms:F0}ms"))}");
    });

    await Results.Run("Q1b", "2-hop jump: bastion -> hop2 -> target", async () =>
    {
        long ob = LogSize("bastion"), oh = LogSize("hop2");
        using var chain = await ChainFactory.ConnectAsync([bastion, hop2, target], null, NewTofu("q1b"), TofuPolicy.AcceptNew, MkSsh);
        var r = chain.Target.RunCommand("echo hello-from-target");
        await Task.Delay(200);
        var lb = Regex.Match(LogSince("bastion", ob), @"server_request_direct_tcpip: .*target \S+ port (\d+)");
        var lh = Regex.Match(LogSince("hop2", oh), @"server_request_direct_tcpip: .*target \S+ port (\d+)");
        bool ok = r.Result.Trim() == "hello-from-target" && lb.Groups[1].Value == "2224" && lh.Groups[1].Value == "2223";
        return Results.Ok(ok,
            $"forwards: {string.Join(", ", chain.Forwards.Select(f => $"{f.BoundHost}:{f.BoundPort}->{f.Host}:{f.Port}"))}\n" +
            $"bastion log: {lb.Value}\nhop2 log: {lh.Value}\n" +
            $"timings: {string.Join("; ", chain.Timings.Select(t => $"{t.hop}@{t.endpoint} {t.ms:F0}ms"))}");
    });

    await Results.Run("Q1c", "one bastion client, two forwards (connection-pool shape), concurrent use", async () =>
    {
        var tofu = NewTofu("q1c");
        using var b = new SshClient(ChainFactory.MakeInfo(Loopback, Bastion, bastion, null));
        b.HostKeyReceived += tofu.Handler(Loopback, Bastion, TofuPolicy.AcceptNew);
        await b.ConnectAsync(default);
        var f1 = new ForwardedPortLocal(Loopback, 0, Loopback, Target); b.AddForwardedPort(f1); f1.Start();
        var f2 = new ForwardedPortLocal(Loopback, 0, Loopback, Hop2); b.AddForwardedPort(f2); f2.Start();
        using var t1 = new SshClient(ChainFactory.MakeInfo(Loopback, (int)f1.BoundPort, target, null));
        using var t2 = new SshClient(ChainFactory.MakeInfo(Loopback, (int)f2.BoundPort, hop2, null));
        t1.HostKeyReceived += tofu.Handler(Loopback, Target, TofuPolicy.AcceptNew);
        t2.HostKeyReceived += tofu.Handler(Loopback, Hop2, TofuPolicy.AcceptNew);
        await Task.WhenAll(t1.ConnectAsync(default), t2.ConnectAsync(default));
        var rs = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => (i % 2 == 0 ? t1 : t2).RunCommand($"echo {i}").Result.Trim())));
        bool ok = f1.BoundPort != f2.BoundPort && rs.SequenceEqual(Enumerable.Range(0, 10).Select(i => i.ToString()));
        return Results.Ok(ok, $"ports {f1.BoundPort},{f2.BoundPort}; 10 concurrent commands -> [{string.Join(",", rs)}]; ForwardedPorts.Count={b.ForwardedPorts.Count()}");
    });

    await Results.Run("Q1d", "security: any local process can connect to the forwarded port", async () =>
    {
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, NewTofu("q1d"), TofuPolicy.AcceptNew, MkSsh);
        var fwd = chain.Forwards[0];
        var accepted = new List<string>();
        fwd.RequestReceived += (_, e) => { lock (accepted) accepted.Add($"{e.OriginatorHost}:{e.OriginatorPort}"); };
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Loopback, (int)fwd.BoundPort);
        var buf = new byte[64];
        int n = await tcp.GetStream().ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var banner = Encoding.ASCII.GetString(buf, 0, n).Trim();
        return new Outcome(banner.StartsWith("SSH-2.0") ? Status.INFO : Status.FAIL,
            $"foreign TcpClient got target banner '{banner}' via the bastion (RequestReceived saw {string.Join(",", accepted)}).\n" +
            "ForwardedPortLocal has no accept filter and cannot be stopped while in use (Q1e) -> guard via RequestReceived or use SingleAcceptBridge (Q1h).");
    });

    await Results.Run("Q1e", "fwd.Stop() after target connected: does the existing session survive?", async () =>
    {
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, NewTofu("q1e"), TofuPolicy.AcceptNew, MkSsh);
        var fwd = chain.Forwards[0];
        var port = (int)fwd.BoundPort;
        var sw = Stopwatch.StartNew();
        fwd.Stop();
        var stopMs = sw.ElapsedMilliseconds;
        string after;
        try { after = chain.Target.RunCommand("echo still-alive").Result.Trim(); }
        catch (Exception ex) { after = "EXC " + ex.GetType().Name + ": " + ex.Message; }
        string reconnect;
        try { using var t = new TcpClient(); await t.ConnectAsync(Loopback, port); reconnect = "connect succeeded (listener still open!)"; }
        catch (SocketException ex) { reconnect = "refused: " + ex.SocketErrorCode; }
        bool ok = after == "still-alive" && reconnect.StartsWith("refused");
        return Results.Ok(ok, $"Stop() took {stopMs}ms; command after Stop: {after}; new TCP connect to {port}: {reconnect}", Status.PARTIAL);
    });

    await Results.Run("Q1f", "jump drops while target shell open -> target detects", async () =>
    {
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, NewTofu("q1f"), TofuPolicy.AcceptNew, MkSsh);
        var t = chain.Target;
        var err = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.ErrorOccurred += (_, e) => err.TrySetResult(e.Exception.GetType().Name + ": " + e.Exception.Message);
        using var shell = t.CreateShellStream("xterm-256color", 80, 24, 0, 0, 4096);
        shell.Closed += (_, _) => closed.TrySetResult();
        await Task.Delay(500);
        var sw = Stopwatch.StartNew();
        chain.Jumps[0].Dispose();           // simulate bastion connection loss
        var first = await Task.WhenAny(err.Task, closed.Task, Task.Delay(10000));
        var ms = sw.ElapsedMilliseconds;
        await Task.Delay(300);
        bool detected = first != null && (err.Task.IsCompleted || closed.Task.IsCompleted);
        return Results.Ok(detected && !t.IsConnected,
            $"after {ms}ms: ErrorOccurred={(err.Task.IsCompleted ? err.Task.Result : "-")} ShellStream.Closed={closed.Task.IsCompleted} target.IsConnected={t.IsConnected}");
    });

    await Results.Run("Q1h", "fallback prototype: single-accept bridge over internal direct-tcpip (reflection)", async () =>
    {
        var tofu = NewTofu("q1h");
        using var b = new SshClient(ChainFactory.MakeInfo(Loopback, Bastion, bastion, null));
        b.HostKeyReceived += tofu.Handler(Loopback, Bastion, TofuPolicy.AcceptNew);
        await b.ConnectAsync(default);
        var bridge = new SingleAcceptBridge(b, Loopback, Target);
        using var t = new SshClient(ChainFactory.MakeInfo(Loopback, bridge.Port, target, null));
        t.HostKeyReceived += tofu.Handler(Loopback, Target, TofuPolicy.AcceptNew);
        var sw = Stopwatch.StartNew();
        await t.ConnectAsync(default);
        var ms = sw.ElapsedMilliseconds;
        var r = t.RunCommand("echo via-bridge").Result.Trim();
        string foreign;
        try { using var x = new TcpClient(); await x.ConnectAsync(Loopback, bridge.Port); foreign = "foreign connect SUCCEEDED"; }
        catch (SocketException ex) { foreign = "foreign connect refused: " + ex.SocketErrorCode; }
        // throughput sanity through the bridge: 20MB exec output
        var sw2 = Stopwatch.StartNew();
        using var cmd = t.CreateCommand("head -c 20000000 /dev/zero");
        var exec = cmd.ExecuteAsync(); long tot = 0; var buf = new byte[65536]; int n;
        while ((n = await cmd.OutputStream.ReadAsync(buf)) > 0) tot += n;
        await exec;
        var mbps = tot / 1048576.0 / sw2.Elapsed.TotalSeconds;
        t.Disconnect();
        await Task.WhenAny(bridge.Pump, Task.Delay(3000));
        var pumpDone = bridge.Pump.IsCompleted;
        bridge.Dispose();
        return Results.Ok(r == "via-bridge" && foreign.Contains("refused"),
            $"target connect {ms}ms via 127.0.0.1:{bridge.Port} (accepted {bridge.AcceptedFrom}); cmd={r}; {foreign}; 20MB exec stream {mbps:F0} MB/s; pump task ended after disconnect={pumpDone} ({bridge.Pump.Status})");
    });

    await Results.Run("Q1g", "8 parallel 2-hop chains (factory under load)", async () =>
    {
        var tofu = NewTofu("q1g");
        var sw = Stopwatch.StartNew();
        var chains = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ChainFactory.ConnectAsync([bastion, hop2, target], null, tofu, TofuPolicy.AcceptNew, MkSsh)));
        var total = sw.ElapsedMilliseconds;
        var ok = chains.All(c => c.Target.RunCommand("echo ok").Result.Trim() == "ok");
        var ports = chains.SelectMany(c => c.Forwards.Select(f => f.BoundPort)).ToList();
        foreach (var c in chains) c.Dispose();
        return Results.Ok(ok && ports.Distinct().Count() == ports.Count,
            $"8 chains in {total}ms, {ports.Count} distinct bound ports; tofu entries={tofu.Hosts.Count} ({string.Join(",", tofu.Hosts)})");
    });
}

// =====================================================================================================
// Q2 host key verification (TOFU keyed by real host:port)
// =====================================================================================================
if (On("Q2"))
{
    var tofu = NewTofu("q2");
    await Results.Run("Q2a", "TOFU accept-new through jump, keyed by real host:port, OpenSSH fingerprint", async () =>
    {
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, tofu, TofuPolicy.AcceptNew, MkSsh);
        var bound = chain.Forwards[0].BoundPort;
        var alg = chain.Target.ConnectionInfo.CurrentHostKeyAlgorithm;
        var pub = alg.Contains("ed25519") ? "ssh_host_ed25519_key.pub" : "ssh_host_rsa_key.pub";
        var (_, kg) = Run("/usr/bin/ssh-keygen", $"-lf {Path.Combine(Work, "target", pub)}");
        var ourFp = Regex.Match(string.Join("\n", tofu.Events), @"\[127\.0\.0\.1\]:2223 \S+ (SHA256:\S+)").Groups[1].Value;
        var (fcode, fout) = Run("/usr/bin/ssh-keygen", $"-F [127.0.0.1]:2223 -f {tofu.FilePath}");
        bool keyedByReal = tofu.Hosts.Contains("[127.0.0.1]:2223") && !tofu.Hosts.Any(h => h.Contains($":{bound}"));
        bool fpOk = ourFp.Length > 0 && kg.Contains(ourFp);
        return Results.Ok(keyedByReal && fpOk && fcode == 0,
            $"forwarded endpoint was 127.0.0.1:{bound}; store hosts = {string.Join(", ", tofu.Hosts)}\n" +
            $"events:\n  {string.Join("\n  ", tofu.Events)}\nnegotiated host key alg: {alg}\n" +
            $"ssh-keygen -lf: {kg}\nssh-keygen -F on our file: exit {fcode} ({fout.Split('\n').FirstOrDefault()})");
    });

    await Results.Run("Q2b", "reconnect with Strict policy -> Match", async () =>
    {
        tofu.Events.Clear();
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, tofu, TofuPolicy.Strict, MkSsh);
        return Results.Ok(tofu.Events.All(e => e.EndsWith("Match")) && tofu.Events.Count == 2, string.Join("\n", tofu.Events));
    });

    await Results.Run("Q2c", "Strict policy + unknown host (AI/MCP rule) -> refused", async () =>
    {
        var empty = NewTofu("q2c");
        try
        {
            using var chain = await ChainFactory.ConnectAsync([bastion, target], null, empty, TofuPolicy.Strict, MkSsh);
            return new Outcome(Status.FAIL, "connected although host unknown");
        }
        catch (SshConnectionException ex)
        {
            return new Outcome(Status.PASS, $"{ex.GetType().Name}: {ex.Message} (DisconnectReason={ex.DisconnectReason})\n{string.Join("\n", empty.Events)}");
        }
    });

    await Results.Run("Q2d", "regenerate target host key -> mismatch -> connection refused", async () =>
    {
        var (code, outp) = Run("/bin/bash", "env.sh regen-target");
        if (code != 0) return new Outcome(Status.FAIL, "regen failed: " + outp);
        tofu.Events.Clear();
        var marker = Path.Combine(Work, "q2d_marker");
        File.Delete(marker);
        try
        {
            using var chain = await ChainFactory.ConnectAsync([bastion, target], null, tofu, TofuPolicy.AcceptNew, MkSsh);
            chain.Target.RunCommand($"touch {marker}");
            return new Outcome(Status.FAIL, "connected despite changed key! " + string.Join("\n", tofu.Events));
        }
        catch (SshConnectionException ex)
        {
            bool ok = tofu.Events.Any(e => e.Contains(":2223") && e.EndsWith("RejectedMismatch")) && !File.Exists(marker);
            return Results.Ok(ok, $"{ex.GetType().Name}: {ex.Message}\n{string.Join("\n", tofu.Events)}\n(regen: {outp.Split('\n').Last()})");
        }
    });

    await Results.Run("Q2e", "HostKeyReceived handler blocks 4s (simulated user prompt) with ConnectionInfo.Timeout=2s", async () =>
    {
        var ci = ChainFactory.MakeInfo(Loopback, Target, target, null);
        ci.Timeout = TimeSpan.FromSeconds(2);
        using var c = new SshClient(ci);
        string thread = "";
        c.HostKeyReceived += (_, e) =>
        {
            thread = $"tid={Environment.CurrentManagedThreadId} pool={Thread.CurrentThread.IsThreadPoolThread} name='{Thread.CurrentThread.Name}'";
            Thread.Sleep(4000);
            e.CanTrust = true;
        };
        var sw = Stopwatch.StartNew();
        try
        {
            await c.ConnectAsync(default);
            return new Outcome(Status.PASS, $"connected after {sw.ElapsedMilliseconds}ms -> a blocking prompt inside the handler is OK; handler ran on {thread}");
        }
        catch (Exception ex)
        {
            return new Outcome(Status.PARTIAL, $"failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}; handler ran on {thread} -> use 2-phase (probe then reconnect) for UI prompts");
        }
    });

    await Results.Run("Q2f", "same 4s blocking prompt with Timeout=30s during connect, lowered to 5s afterwards", async () =>
    {
        var ci = ChainFactory.MakeInfo(Loopback, Target, target, null);
        ci.Timeout = TimeSpan.FromSeconds(30);
        using var c = new SshClient(ci);
        c.HostKeyReceived += (_, e) => { Thread.Sleep(4000); e.CanTrust = true; };
        var sw = Stopwatch.StartNew();
        await c.ConnectAsync(default);
        var ms = sw.ElapsedMilliseconds;
        ci.Timeout = TimeSpan.FromSeconds(5);
        var r = c.RunCommand("echo ok").Result.Trim();
        return Results.Ok(r == "ok", $"connected after {ms}ms, then Timeout lowered to {c.ConnectionInfo.Timeout.TotalSeconds}s, cmd={r} (sshd LoginGraceTime=120s still bounds the prompt)");
    });
}

// =====================================================================================================
// Q3 SOCKS5 proxy
// =====================================================================================================
if (On("Q3"))
{
    var socks = new ProxySpec(ProxyTypes.Socks5, Loopback, Socks);
    await Results.Run("Q3a", "SOCKS5 (ssh -D via bastion) -> target", async () =>
    {
        var ob = LogSize("bastion");
        var tofu = NewTofu("q3a");
        using var chain = await ChainFactory.ConnectAsync([target], socks, tofu, TofuPolicy.AcceptNew, MkSsh);
        var r = chain.Target.RunCommand("echo via-socks").Result.Trim();
        await Task.Delay(200);
        var m = Regex.Match(LogSince("bastion", ob), @"server_request_direct_tcpip: [^\n]*");
        return Results.Ok(r == "via-socks" && m.Success && chain.Target.ConnectionInfo.ProxyType == ProxyTypes.Socks5,
            $"result={r}; bastion log: {m.Value}; tofu: {string.Join(", ", tofu.Hosts)}; {chain.Timings[0].ms:F0}ms");
    });

    await Results.Run("Q3b", "SOCKS5 + jump: proxy -> hop2 -> target (proxy only on first hop)", async () =>
    {
        long ob = LogSize("bastion"), oh = LogSize("hop2");
        var tofu = NewTofu("q3b");
        using var chain = await ChainFactory.ConnectAsync([hop2, target], socks, tofu, TofuPolicy.AcceptNew, MkSsh);
        var r = chain.Target.RunCommand("echo proxy+jump").Result.Trim();
        await Task.Delay(200);
        var lb = Regex.Match(LogSince("bastion", ob), @"server_request_direct_tcpip: .*target \S+ port (\d+)");
        var lh = Regex.Match(LogSince("hop2", oh), @"server_request_direct_tcpip: .*target \S+ port (\d+)");
        bool ok = r == "proxy+jump" && lb.Groups[1].Value == "2224" && lh.Groups[1].Value == "2223"
                  && chain.Jumps[0].ConnectionInfo.ProxyType == ProxyTypes.Socks5 && chain.Target.ConnectionInfo.ProxyType == ProxyTypes.None;
        return Results.Ok(ok, $"result={r}\nbastion(socks) log: {lb.Value}\nhop2 log: {lh.Value}\ntofu: {string.Join(", ", tofu.Hosts)}");
    });

    await Results.Run("Q3c", "dead proxy (port 1081) fails fast with clear exception", async () =>
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var chain = await ChainFactory.ConnectAsync([target], socks with { Port = 1081 }, NewTofu("q3c"), TofuPolicy.AcceptNew, MkSsh);
            return new Outcome(Status.FAIL, "connected?!");
        }
        catch (Exception ex) { return new Outcome(Status.PASS, $"{ex.GetType().FullName}: {ex.Message} after {sw.ElapsedMilliseconds}ms"); }
    });
}

// =====================================================================================================
// Q4 authentication
// =====================================================================================================
if (On("Q4"))
{
    async Task<string> TryAuth(string key, string? pass, bool normalize = true)
    {
        try
        {
            var ci = new ConnectionInfo(Loopback, Target, User,
                new PrivateKeyAuthenticationMethod(User, Auth.LoadKey(Env.Key(key), pass, normalize)));
            ci.Timeout = TimeSpan.FromSeconds(10);
            using var c = new SshClient(ci);
            c.HostKeyReceived += (_, e) => e.CanTrust = true;
            await c.ConnectAsync(default);
            return "OK " + c.RunCommand("echo authed").Result.Trim();
        }
        catch (Exception ex) { return $"EXC {ex.GetType().Name}: {ex.Message}"; }
    }
    async Task<Outcome> Expect(string key, string? pass, Func<string, bool> pred, bool normalize = true, string? logRe = null)
    {
        var off = LogSize("target");
        var r = await TryAuth(key, pass, normalize);
        await Task.Delay(150);
        var extra = "";
        if (logRe != null)
            extra = "\ntarget log: " + string.Join(" | ", Regex.Matches(LogSince("target", off), logRe).Select(m => m.Value.Trim()).Distinct());
        return Results.Ok(pred(r), r + extra);
    }
    await Results.Run("Q4a", "ed25519 OpenSSH key WITH passphrase", () => Expect("id_ed25519_pass", Passphrase, r => r == "OK authed", logRe: @"Accepted publickey[^\n]*"));
    await Results.Run("Q4b", "ed25519 wrong passphrase -> distinguishable exception", () => Expect("id_ed25519_pass", "nope", r => r.StartsWith("EXC")));
    await Results.Run("Q4c", "encrypted key + empty passphrase (normalized to null) -> 'needs passphrase' exception", () => Expect("id_ed25519_pass", "", r => r.Contains(nameof(SshPassPhraseNullOrEmptyException))));
    await Results.Run("Q4d", "unencrypted key + empty passphrase passed RAW (\"\")", () => Expect("id_ed25519", "", r => r == "OK authed", normalize: false));
    await Results.Run("Q4e", "unencrypted key + passphrase null", () => Expect("id_ed25519", null, r => r == "OK authed"));
    await Results.Run("Q4f", "RSA 3072 OpenSSH key (no passphrase) -> rsa-sha2-*", () => Expect("id_rsa", null, r => r == "OK authed", logRe: @"(Accepted publickey[^\n]*|userauth_pubkey: [^\n]*pkalg[^\n]*)"));
    await Results.Run("Q4g", "RSA OpenSSH key with passphrase", () => Expect("id_rsa_pass", Passphrase, r => r == "OK authed"));
    await Results.Run("Q4h", "RSA legacy PEM (BEGIN RSA PRIVATE KEY, encrypted)", () => Expect("id_rsa_pem_pass", Passphrase, r => r == "OK authed"));
}

// =====================================================================================================
// Q5 ShellStream
// =====================================================================================================
if (On("Q5"))
{
    await Results.Run("Q5a", "ShellStream xterm-256color, resize, exit 3 + exit status, no leaks", async () =>
    {
        await Task.Delay(200); GC.Collect(); GC.WaitForPendingFinalizers();
        int fdA = OpenFdCount(), thA = ThreadCount();
        var sb = new StringBuilder();
        var notes = new List<string>();
        int? exitStatus = null; string? exitSignal = null; string rawReq = "";
        Task reader; bool closedFired; long readerBytes = 0; TimeSpan closeLatency;
        {
            using var c = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
            c.HostKeyReceived += (_, e) => e.CanTrust = true;
            await c.ConnectAsync(default);
            var session = Internals.GetSession(c);
            var shell = c.CreateShellStream("xterm-256color", 80, 24, 0, 0, 65536);
            uint ch = Internals.ShellChannelNumber(shell);
            session.ChannelRequestReceived += (_, e) =>
            {
                if (e.Message.LocalChannelNumber != ch) return;
                rawReq += $"{e.Message.RequestName}[{Convert.ToHexString(e.Message.RequestData ?? [])}] ";
                var d = e.Message.RequestData;
                if (e.Message.RequestName == "exit-status" && d is { Length: >= 4 }) exitStatus = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(d.Length - 4));
                if (e.Message.RequestName == "exit-signal") exitSignal = "signal";
            };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            reader = Task.Run(async () =>
            {
                var buf = new byte[8192]; int n;
                while ((n = await shell.ReadAsync(buf)) > 0) { lock (sb) sb.Append(Encoding.UTF8.GetString(buf, 0, n)); readerBytes += n; }
            });
            async Task<Match> WaitFor(string re, int ms = 8000)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    lock (sb) { var m = Regex.Match(sb.ToString(), re); if (m.Success) return m; }
                    await Task.Delay(30);
                }
                return Match.Empty;
            }
            shell.WriteLine("echo M1:$TERM:$(stty size)");
            var m1 = await WaitFor(@"M1:(\S+):(\d+) (\d+)");
            notes.Add($"initial: TERM={m1.Groups[1].Value} stty size={m1.Groups[2].Value} {m1.Groups[3].Value}");
            shell.ChangeWindowSize(132, 43, 0, 0);
            await Task.Delay(100);
            shell.WriteLine("echo M2:$(stty size)");
            var m2 = await WaitFor(@"M2:(\d+) (\d+)");
            notes.Add($"after ChangeWindowSize(132,43): stty size={m2.Groups[1].Value} {m2.Groups[2].Value}");
            shell.WriteLine("echo M3:$(printf '\\344\\270\\255\\346\\226\\207')");
            var m3 = await WaitFor(@"M3:(\p{IsCJKUnifiedIdeographs}+)");
            notes.Add($"utf8 roundtrip: {m3.Groups[1].Value}");
            var swc = Stopwatch.StartNew();
            shell.WriteLine("exit 3");
            await Task.WhenAny(closed.Task, Task.Delay(8000));
            closeLatency = swc.Elapsed;
            closedFired = closed.Task.IsCompleted;
            await Task.WhenAny(reader, Task.Delay(3000));
            notes.Add($"Closed event={closedFired} after {closeLatency.TotalMilliseconds:F0}ms; reader task completed (Read returned 0)={reader.IsCompleted}; bytes read={readerBytes}");
            notes.Add($"channel requests seen on ch{ch}: {rawReq}");
            notes.Add($"client.IsConnected after shell exit: {c.IsConnected} (session stays up; only channel closed)");
            shell.Dispose();
            c.Disconnect();
            bool sizesOk = m1.Groups[1].Value == "xterm-256color" && m1.Groups[2].Value == "24" && m1.Groups[3].Value == "80"
                        && m2.Groups[1].Value == "43" && m2.Groups[2].Value == "132";
            notes.Insert(0, $"sizesOk={sizesOk}");
        }
        await Task.Delay(500); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int fdB = OpenFdCount(), thB = ThreadCount();
        var (_, kids) = Run("/usr/bin/pgrep", $"-P {File.ReadAllText(Path.Combine(Work, "target", "sshd.pid")).Trim()}");
        notes.Add($"fds {fdA}->{fdB}, threads {thA}->{thB}, target sshd children left: '{kids}'");
        notes.Add($"exitStatus={exitStatus?.ToString() ?? "null"} exitSignal={exitSignal ?? "null"}");
        bool ok = notes[0] == "sizesOk=True" && closedFired && reader.IsCompleted && exitStatus == 3 && kids == "";
        return Results.Ok(ok, string.Join("\n", notes), Status.PARTIAL);
    });

    await Results.Run("Q5c", "ShellStream back-pressure: remote prints 100MB, client does not read for 3s", async () =>
    {
        using var c = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        c.HostKeyReceived += (_, e) => e.CanTrust = true;
        await c.ConnectAsync(default);
        long gc0 = GC.GetTotalMemory(true);
        using var shell = c.CreateShellStream("xterm-256color", 80, 24, 0, 0, 65536);
        shell.WriteLine("head -c 100000000 /dev/zero | tr '\\0' 'a'; echo; echo M9:$((6*7))");
        await Task.Delay(3000);
        long heap = GC.GetTotalMemory(false) - gc0;
        var (_, ps) = Run("/bin/ps", "-axo pid,command");
        bool remoteStillRunning = ps.Contains("head -c 100000000");
        var buf = new byte[1 << 16]; long tot = 0; var sw = Stopwatch.StartNew(); var tail = "";
        while (sw.ElapsedMilliseconds < 20000)
        {
            int n;
            try { n = await shell.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { break; }
            if (n == 0) break; tot += n;
            tail += Encoding.ASCII.GetString(buf, 0, n);
            if (tail.Contains("M9:42")) break;
            if (tail.Length > 4096) tail = tail[^200..];
        }
        bool bp = heap < 20_000_000 && remoteStillRunning;
        return new Outcome(bp ? Status.PASS : Status.PARTIAL,
            $"after 3s unread: heap +{heap / 1048576}MB, remote producer still running (blocked)={remoteStillRunning}; drained {tot / 1048576}MB in {sw.ElapsedMilliseconds}ms ({tot / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MB/s)");
    });

    await Results.Run("Q5b","leak check: 50x (connect, shell, exit 3, dispose) -> fds/threads stable", async () =>
    {
        async Task Cycle()
        {
            using var c = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
            c.HostKeyReceived += (_, e) => e.CanTrust = true;
            await c.ConnectAsync(default);
            using var shell = c.CreateShellStream("xterm-256color", 80, 24, 0, 0, 4096);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Closed += (_, _) => closed.TrySetResult();
            var reader = Task.Run(async () => { var b = new byte[4096]; while (await shell.ReadAsync(b) > 0) { } });
            shell.WriteLine("exit 3");
            await Task.WhenAll(closed.Task, reader).WaitAsync(TimeSpan.FromSeconds(10));
            c.Disconnect();
        }
        async Task<(int fd, int th)> Snap() { await Task.Delay(300); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return (OpenFdCount(), ThreadCount()); }
        await Cycle(); await Cycle();
        var a = await Snap();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 48; i++) await Cycle();
        var per = sw.ElapsedMilliseconds / 48.0;
        var b = await Snap();
        var (_, kids) = Run("/usr/bin/pgrep", $"-P {File.ReadAllText(Path.Combine(Work, "target", "sshd.pid")).Trim()}");
        return Results.Ok(b.fd <= a.fd + 2 && b.th <= a.th + 4 && kids == "",
            $"after 2 cycles: fds={a.fd} threads={a.th}; after 50 cycles: fds={b.fd} threads={b.th}; {per:F0}ms/cycle; server sessions left='{kids}'");
    });
}

// =====================================================================================================
// Q6 exec
// =====================================================================================================
if (On("Q6"))
{
    using var c = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
    c.HostKeyReceived += (_, e) => e.CanTrust = true;
    await c.ConnectAsync(default);
    bool Alive(string pattern) => Run("/usr/bin/pgrep", $"-f \"{pattern}\"").code == 0;

    await Results.Run("Q6a", "exit status + stderr", async () =>
    {
        using var cmd = c.CreateCommand("echo out; echo err >&2; exit 7");
        await cmd.ExecuteAsync();
        return Results.Ok(cmd.ExitStatus == 7 && cmd.Result.Trim() == "out" && cmd.Error.Trim() == "err", $"ExitStatus={cmd.ExitStatus} Result='{cmd.Result.Trim()}' Error='{cmd.Error.Trim()}'");
    });

    await Results.Run("Q6b", "CommandTimeout=2s on `sleep 30.111` (async)", async () =>
    {
        using var cmd = c.CreateCommand("sleep 30.111");
        cmd.CommandTimeout = TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();
        string ex = "none";
        try { await cmd.ExecuteAsync(); } catch (Exception e) { ex = e.GetType().Name + ": " + e.Message; }
        var ms = sw.ElapsedMilliseconds;
        await Task.Delay(500);
        var alive = Alive("sleep 30.111");
        return Results.Ok(ex.StartsWith(nameof(SshOperationTimeoutException)) && ms < 3500 && !alive,
            $"{ex} after {ms}ms; ExitStatus={cmd.ExitStatus?.ToString() ?? "null"} ExitSignal={cmd.ExitSignal ?? "null"}; remote sleep still alive={alive}", Status.PARTIAL);
    });

    await Results.Run("Q6c", "CancellationToken cancel after 1.5s on `sleep 30.222`", async () =>
    {
        using var cmd = c.CreateCommand("sleep 30.222");
        using var cts = new CancellationTokenSource(1500);
        var sw = Stopwatch.StartNew();
        string ex = "none";
        try { await cmd.ExecuteAsync(cts.Token); } catch (Exception e) { ex = e.GetType().Name + ": " + e.Message; }
        var ms = sw.ElapsedMilliseconds;
        await Task.Delay(500);
        var alive = Alive("sleep 30.222");
        return Results.Ok(ex.Contains("Cancel") && ms < 3000 && !alive,
            $"{ex} after {ms}ms; ExitStatus={cmd.ExitStatus?.ToString() ?? "null"} ExitSignal={cmd.ExitSignal ?? "null"}; remote sleep still alive={alive}", Status.PARTIAL);
    });

    await Results.Run("Q6d", "cap `yes | head -c 5000000` at 64KB while streaming", async () =>
    {
        const int cap = 64 * 1024;
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        using var cmd = c.CreateCommand("yes | head -c 5000000");
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var exec = cmd.ExecuteAsync(cts.Token);
        var buf = new byte[16 * 1024];
        long got = 0;
        while (got < cap)
        {
            int n = await cmd.OutputStream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, cap - got)));
            if (n == 0) break;
            got += n;
        }
        var capMs = sw.ElapsedMilliseconds;
        cts.Cancel();
        string ex = "none";
        try { await exec.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) { ex = e.GetType().Name; }
        var doneMs = sw.ElapsedMilliseconds;
        long alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
        await Task.Delay(300);
        var yesAlive = Run("/usr/bin/pgrep", "-x yes").code == 0;
        // baseline: read everything
        long alloc1 = GC.GetTotalAllocatedBytes(true);
        using var all = c.CreateCommand("yes | head -c 5000000");
        var swAll = Stopwatch.StartNew();
        var execAll = all.ExecuteAsync();
        long total = 0; int k;
        while ((k = await all.OutputStream.ReadAsync(buf)) > 0) total += k;
        await execAll;
        long allocAll = GC.GetTotalAllocatedBytes(true) - alloc1;
        bool ok = got == cap && doneMs < 3000 && alloc < 3_000_000;
        return Results.Ok(ok,
            $"capped: read {got} bytes in {capMs}ms, cancel->done {doneMs - capMs}ms ({ex}), managed alloc during capped run={alloc / 1024}KB, remote `yes` alive after={yesAlive}\n" +
            $"uncapped baseline: {total} bytes in {swAll.ElapsedMilliseconds}ms, alloc={allocAll / 1024}KB, ExitStatus={all.ExitStatus}", Status.PARTIAL);
    });
    await Results.Run("Q6e", "back-pressure: produce 100MB, read nothing for 3s -> how much is buffered client-side?", async () =>
    {
        long gc0 = GC.GetTotalMemory(true);
        using var cmd = c.CreateCommand("head -c 100000000 /dev/zero; echo done >&2");
        var exec = cmd.ExecuteAsync();
        await Task.Delay(3000);
        long buffered = GC.GetTotalMemory(false) - gc0;
        long pipeLen = -1; try { pipeLen = cmd.OutputStream.Length; } catch { }
        bool finished = exec.IsCompleted;
        // drain
        var buf = new byte[1 << 16]; long tot = 0; int n;
        while ((n = await cmd.OutputStream.ReadAsync(buf)) > 0) tot += n;
        await exec;
        bool backpressure = buffered < 20_000_000 && !finished;
        return new Outcome(backpressure ? Status.PASS : Status.PARTIAL,
            $"after 3s without reading: managed heap +{buffered / 1048576}MB, OutputStream.Length={pipeLen}, command finished={finished}; drained {tot / 1048576}MB afterwards\n" +
            (backpressure ? "-> channel window limits in-flight data (back-pressure works)" : "-> NO back-pressure: SSH.NET buffers everything in PipeStream; cap must cancel early / consumer must keep up"));
    });
    c.Disconnect();
}

// =====================================================================================================
// Q7 SFTP
// =====================================================================================================
if (On("Q7"))
{
    var localDir = Path.Combine(Work, "sftp"); Directory.CreateDirectory(localDir);
    var remoteDir = Path.Combine(Work, "remote"); Directory.CreateDirectory(remoteDir);   // target is this machine
    var src = Path.Combine(localDir, "local_200m.bin");
    const long Size = 200L * 1024 * 1024;
    if (!File.Exists(src) || new FileInfo(src).Length != Size)
    {
        using var fs = File.Create(src); var chunk = new byte[1 << 20];
        for (int i = 0; i < 200; i++) { Random.Shared.NextBytes(chunk); fs.Write(chunk); }
    }
    string Sha(string p) { using var f = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(f))[..16]; }
    var srcSha = Sha(src);

    async Task<string> Upload(SftpClient s, string remote)
    {
        int calls = 0; ulong last = 0;
        var prog = new SyncProgress<UploadFileProgressReport>(r => { calls++; last = r.TotalBytesUploaded; });
        await using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var sw = Stopwatch.StartNew();
        await s.UploadFileAsync(fs, remote, prog, default);
        var sec = sw.Elapsed.TotalSeconds;
        return $"upload {Size / 1048576}MB in {sec:F2}s = {Size / 1048576.0 / sec:F1} MB/s, progress callbacks={calls}, last={last}";
    }
    async Task<string> Download(SftpClient s, string remote, string local)
    {
        int calls = 0; ulong last = 0;
        var prog = new SyncProgress<DownloadFileProgressReport>(r => { calls++; last = r.TotalBytesDownloaded; });
        double sec;
        await using (var fs = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var sw = Stopwatch.StartNew();
            await s.DownloadFileAsync(remote, fs, prog, default);
            sec = sw.Elapsed.TotalSeconds;
        }
        return $"download in {sec:F2}s = {Size / 1048576.0 / sec:F1} MB/s, callbacks={calls}, last={last}, sha match={Sha(local) == srcSha}";
    }

    await Results.Run("Q7a", "SFTP 200MB streaming upload/download, direct", async () =>
    {
        using var s = new SftpClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        s.HostKeyReceived += (_, e) => e.CanTrust = true;
        await s.ConnectAsync(default);
        var peak0 = Environment.WorkingSet;
        var u = await Upload(s, Path.Combine(remoteDir, "up.bin"));
        var d = await Download(s, Path.Combine(remoteDir, "up.bin"), Path.Combine(localDir, "down.bin"));
        var ws = Environment.WorkingSet;
        return Results.Ok(d.Contains("sha match=True"), $"BufferSize={s.BufferSize} ProtocolVersion={s.ProtocolVersion}\n{u}\n{d}\nworking set {peak0 / 1048576}MB -> {ws / 1048576}MB (not buffering 200MB)");
    });

    await Results.Run("Q7b", "SFTP 200MB via jump (bastion -> target)", async () =>
    {
        using var chain = await ChainFactory.ConnectAsync([bastion, target], null, NewTofu("q7b"), TofuPolicy.AcceptNew, ci => new SftpClient(ci));
        var u = await Upload(chain.Target, Path.Combine(remoteDir, "up_jump.bin"));
        var d = await Download(chain.Target, Path.Combine(remoteDir, "up_jump.bin"), Path.Combine(localDir, "down_jump.bin"));
        return Results.Ok(d.Contains("sha match=True"), $"{u}\n{d}");
    });

    await Results.Run("Q7c", "manual streaming via OpenAsync + CopyToAsync(256KB) and BufferSize=64KB variant", async () =>
    {
        using var s = new SftpClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        s.HostKeyReceived += (_, e) => e.CanTrust = true;
        await s.ConnectAsync(default);
        var sw = Stopwatch.StartNew();
        await using (var rs = await s.OpenAsync(Path.Combine(remoteDir, "manual.bin"), FileMode.Create, FileAccess.Write, default))
        await using (var fs = File.OpenRead(src)) await fs.CopyToAsync(rs, 256 * 1024);
        var t1 = sw.Elapsed.TotalSeconds;
        sw.Restart();
        await using (var rs = await s.OpenAsync(Path.Combine(remoteDir, "manual.bin"), FileMode.Open, FileAccess.Read, default))
        await using (var fs = File.Create(Path.Combine(localDir, "manual_down.bin"))) await rs.CopyToAsync(fs, 256 * 1024);
        var t2 = sw.Elapsed.TotalSeconds;
        var match = Sha(Path.Combine(localDir, "manual_down.bin")) == srcSha;
        s.BufferSize = 64 * 1024;
        var u64 = await Upload(s, Path.Combine(remoteDir, "up64.bin"));
        return Results.Ok(match, $"OpenAsync write {200 / t1:F1} MB/s, read {200 / t2:F1} MB/s, sha match={match}\nBufferSize=64KB: {u64}");
    });

    await Results.Run("Q7d", "cancel upload mid-way via CancellationToken", async () =>
    {
        using var s = new SftpClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        s.HostKeyReceived += (_, e) => e.CanTrust = true;
        await s.ConnectAsync(default);
        using var cts = new CancellationTokenSource();
        var prog = new SyncProgress<UploadFileProgressReport>(r => { if (r.TotalBytesUploaded > 30_000_000) cts.Cancel(); });
        await using var fs = File.OpenRead(src);
        var sw = Stopwatch.StartNew();
        string ex = "none";
        try { await s.UploadFileAsync(fs, Path.Combine(remoteDir, "cancel.bin"), prog, cts.Token); } catch (Exception e) { ex = e.GetType().Name; }
        var partial = new FileInfo(Path.Combine(remoteDir, "cancel.bin")).Length;
        var still = s.IsConnected && (await s.ExistsAsync(remoteDir, default));
        return Results.Ok(ex.Contains("Cancel") && still, $"{ex} after {sw.ElapsedMilliseconds}ms; partial remote file {partial / 1048576}MB (caller must delete); client still usable={still}");
    });

    await Results.Run("Q7e", "recursive delete of nested dir (with symlink that must NOT be followed)", async () =>
    {
        using var s = new SftpClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        s.HostKeyReceived += (_, e) => e.CanTrust = true;
        await s.ConnectAsync(default);
        var root = Path.Combine(remoteDir, "tree");
        var sentinel = Path.Combine(remoteDir, "sentinel"); Directory.CreateDirectory(sentinel); File.WriteAllText(Path.Combine(sentinel, "keep.txt"), "keep");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        int files = 0;
        foreach (var d in new[] { "", "a", "a/b", "a/b/c", "x", "x/y" })
        {
            await s.CreateDirectoryAsync(Path.Combine(root, d).TrimEnd('/'), default);
            for (int i = 0; i < 3; i++) { s.WriteAllText(Path.Combine(root, d, $"f{i}.txt"), "data"); files++; }
        }
        string symlinkNote;
        try { s.SymbolicLink(sentinel, Path.Combine(root, "a", "link_to_sentinel")); symlinkNote = "SymbolicLink(path: target, linkPath: link) ok"; }
        catch (Exception e)
        {
            // OpenSSH sftp-server has the SSH_FXP_SYMLINK argument order reversed vs the draft; try swapped
            symlinkNote = $"SymbolicLink(target, link) -> {e.GetType().Name}: {e.Message}; retry swapped";
            s.SymbolicLink(Path.Combine(root, "a", "link_to_sentinel"), sentinel);
        }
        var linkTarget = File.ResolveLinkTarget(Path.Combine(root, "a", "link_to_sentinel"), false)?.FullName;
        string builtin;
        try { await s.DeleteDirectoryAsync(root, default); builtin = "DeleteDirectoryAsync on non-empty dir SUCCEEDED?!"; }
        catch (Exception e) { builtin = $"DeleteDirectoryAsync(non-empty) -> {e.GetType().Name}: {e.Message}"; }
        var sw = Stopwatch.StartNew();
        int deleted = await RecursiveDeleteAsync(s, root, default);
        bool ok = !Directory.Exists(root) && File.Exists(Path.Combine(sentinel, "keep.txt"));
        return Results.Ok(ok, $"{builtin}\n{symlinkNote}; resulting link -> {linkTarget} (points to sentinel={linkTarget == sentinel})\n" +
            $"RecursiveDeleteAsync removed {deleted} entries ({files} files + 1 link + 6 dirs expected) in {sw.ElapsedMilliseconds}ms; tree gone={!Directory.Exists(root)}; sentinel intact={File.Exists(Path.Combine(sentinel, "keep.txt"))}");
    });

    await Results.Run("Q7g", "DANGER check: deleting a symlink-to-FILE via SSH.NET — link or target removed?", async () =>
    {
        using var s = new SftpClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        s.HostKeyReceived += (_, e) => e.CanTrust = true;
        await s.ConnectAsync(default);
        var d = Path.Combine(remoteDir, "linktest"); if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d);
        var rows = new List<string>();
        var variants = new (string name, Func<string, Task> del)[]
        {
            ("DeleteFileAsync", p => s.DeleteFileAsync(p, default)),
            ("DeleteFile (sync)", p => { s.DeleteFile(p); return Task.CompletedTask; }),
            ("DeleteAsync", p => s.DeleteAsync(p, default)),
            ("ISftpFile.DeleteAsync (from ListDirectory entry)", async p =>
            {
                await foreach (var e in s.ListDirectoryAsync(Path.GetDirectoryName(p)!, default))
                    if (e.FullName == p) { await e.DeleteAsync(default); return; }
                throw new FileNotFoundException(p);
            }),
        };
        bool anyTargetLost = false;
        foreach (var (name, del) in variants)
        {
            var tgt = Path.Combine(d, $"target_{rows.Count}.txt"); File.WriteAllText(tgt, "precious");
            var link = Path.Combine(d, $"link_{rows.Count}"); File.CreateSymbolicLink(link, tgt);
            string ex = "ok";
            try { await del(link); } catch (Exception e) { ex = e.GetType().Name + ": " + e.Message; }
            bool linkGone = !Directory.EnumerateFileSystemEntries(d).Contains(link);
            bool targetGone = !File.Exists(tgt);
            anyTargetLost |= targetGone;
            rows.Add($"{name,-48} -> {ex,-12} linkRemoved={linkGone} TARGET_REMOVED={targetGone}");
        }
        // dangling link (realpath fails) for comparison
        var dl = Path.Combine(d, "dangling"); File.CreateSymbolicLink(dl, Path.Combine(d, "nope"));
        string dex = "ok"; try { await s.DeleteFileAsync(dl, default); } catch (Exception e) { dex = e.Message; }
        rows.Add($"{"DeleteFileAsync on dangling link",-48} -> {dex,-12} linkRemoved={!Directory.EnumerateFileSystemEntries(d).Contains(dl)}");
        // rename a symlink
        {
            var tg = Path.Combine(d, "rn_target.txt"); File.WriteAllText(tg, "precious"); var lk = Path.Combine(d, "rn_link"); File.CreateSymbolicLink(lk, tg);
            string ex = "ok"; try { await s.RenameFileAsync(lk, Path.Combine(d, "rn_link_renamed"), default); } catch (Exception e) { ex = e.Message; }
            var names = string.Join(",", Directory.EnumerateFileSystemEntries(d, "rn_*").Select(Path.GetFileName).Order());
            rows.Add($"{"RenameFileAsync(link, link_renamed)",-48} -> {ex,-12} entries now: {names} (renamed_is_link={File.ResolveLinkTarget(Path.Combine(d, "rn_link_renamed"), false) != null})");
        }
        // ISftpFile.MoveTo on a listed symlink entry
        {
            var tg = Path.Combine(d, "mv_target.txt"); File.WriteAllText(tg, "precious"); var lk = Path.Combine(d, "mv_link"); File.CreateSymbolicLink(lk, tg);
            string ex = "ok";
            try { await foreach (var e in s.ListDirectoryAsync(d, default)) if (e.Name == "mv_link") { e.MoveTo(Path.Combine(d, "mv_link_renamed")); break; } }
            catch (Exception e) { ex = e.Message; }
            var names = string.Join(",", Directory.EnumerateFileSystemEntries(d, "mv_*").Select(Path.GetFileName).Order());
            rows.Add($"{"ISftpFile.MoveTo (listed link entry)",-48} -> {ex,-12} entries now: {names} (renamed_is_link={File.ResolveLinkTarget(Path.Combine(d, "mv_link_renamed"), false) != null})");
        }
        // rmdir a symlink pointing to an empty dir
        {
            var tg = Path.Combine(d, "empty_dir"); Directory.CreateDirectory(tg); var lk = Path.Combine(d, "dir_link"); Directory.CreateSymbolicLink(lk, tg);
            string ex = "ok"; try { await s.DeleteDirectoryAsync(lk, default); } catch (Exception e) { ex = e.Message; }
            rows.Add($"{"DeleteDirectoryAsync(link -> empty dir)",-48} -> {ex,-12} linkRemoved={!Directory.EnumerateFileSystemEntries(d).Contains(lk)} TARGET_DIR_REMOVED={!Directory.Exists(tg)}");
        }
        // exec workaround
        using var ssh = new SshClient(ChainFactory.MakeInfo(Loopback, Target, target, null));
        ssh.HostKeyReceived += (_, e) => e.CanTrust = true; await ssh.ConnectAsync(default);
        var t2 = Path.Combine(d, "t_exec.txt"); File.WriteAllText(t2, "precious"); var l2 = Path.Combine(d, "l_exec"); File.CreateSymbolicLink(l2, t2);
        var rr = ssh.RunCommand($"rm -f -- '{l2}'");
        rows.Add($"{"exec rm -f -- link",-48} -> exit {rr.ExitStatus,-7} linkRemoved={!Directory.EnumerateFileSystemEntries(d).Contains(l2)} TARGET_REMOVED={!File.Exists(t2)}");
        return new Outcome(anyTargetLost ? Status.FAIL : Status.INFO, string.Join("\n", rows) + (anyTargetLost ? "\n!!! SSH.NET deletes the symlink TARGET (path canonicalized via realpath before remove)" : ""));
    });

    await Results.Run("Q7f", "async API surface of SftpClient", () =>
    {
        var names = typeof(SftpClient).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.EndsWith("Async")).Select(m => m.Name).Distinct().OrderBy(x => x);
        var syncOnly = new[] { "SymbolicLink", "ChangePermissions", "SetAttributes", "SynchronizeDirectories", "ReadAllBytes", "WriteAllBytes", "SetLastWriteTime" };
        return Task.FromResult(new Outcome(Status.INFO, $"async: {string.Join(", ", names)}\nsync-only (no *Async): {string.Join(", ", syncOnly)}"));
    });
    foreach (var f in new[] { "down.bin", "down_jump.bin", "manual_down.bin" }) File.Delete(Path.Combine(localDir, f));
    foreach (var f in Directory.GetFiles(remoteDir, "*.bin")) File.Delete(f);
}

// =====================================================================================================
// Q8 keepalive
// =====================================================================================================
if (On("Q8"))
{
    await Results.Run("Q8a", "KeepAliveInterval=1s: what is sent; dead peer (SIGSTOP server) detection", async () =>
    {
        var logs = new CaptureLoggerFactory();
        var ci = ChainFactory.MakeInfo(Loopback, Target, target, null);
        ci.LoggerFactory = logs;
        ci.Timeout = TimeSpan.FromSeconds(3);
        using var c = new SshClient(ci) { KeepAliveInterval = TimeSpan.FromSeconds(1) };
        c.HostKeyReceived += (_, e) => e.CanTrust = true;
        var err = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.ErrorOccurred += (_, e) => err.TrySetResult(e.Exception.GetType().Name + ": " + e.Exception.Message);
        await c.ConnectAsync(default);
        lock (logs.Lines) logs.Lines.Clear();
        await Task.Delay(3500);
        string[] ka; lock (logs.Lines) ka = logs.Lines.Where(l => l.Contains("Ignore", StringComparison.OrdinalIgnoreCase) || l.Contains("keep", StringComparison.OrdinalIgnoreCase)).ToArray();
        var sample = ka.FirstOrDefault() ?? "(no keepalive log lines; total log lines=" + logs.Lines.Count + ")";
        // dead peer: freeze the server side of this connection
        var listener = File.ReadAllText(Path.Combine(Work, "target", "sshd.pid")).Trim();
        var kids = new List<string>(); var queue = new Queue<string>([listener]);
        while (queue.Count > 0)
            foreach (var k in Run("/usr/bin/pgrep", $"-P {queue.Dequeue()}").output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { kids.Add(k); queue.Enqueue(k); }
        string dead;
        try
        {
            foreach (var k in kids) Run("/bin/kill", $"-STOP {k}");
            var w = await Task.WhenAny(err.Task, Task.Delay(6000));
            dead = err.Task.IsCompleted ? $"ErrorOccurred within 6s: {err.Task.Result}" : $"NO ErrorOccurred within 6s of SIGSTOP (IsConnected={c.IsConnected})";
            var sw = Stopwatch.StartNew();
            try { await Task.Run(() => c.RunCommand("true")).WaitAsync(TimeSpan.FromSeconds(10)); dead += "; probe exec succeeded?!"; }
            catch (Exception e) { dead += $"; probe exec -> {e.GetType().Name} after {sw.ElapsedMilliseconds}ms"; }
        }
        finally { foreach (var k in kids) Run("/bin/kill", $"-CONT {k}"); }
        return new Outcome(ka.Length >= 2 ? Status.PARTIAL : Status.FAIL,
            $"{ka.Length} keepalive-related log lines in 3.5s; sample: {sample}\nserver session pids frozen (whole subtree): {string.Join(",", kids)} [{string.Join(" | ", kids.Select(k => Run("/bin/ps", $"-o command= -p {k}").output))}]\n{dead}");
    });
}

// ---------------------------------------------------------------- global leak check + summary
await Task.Delay(800); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
int fd1 = OpenFdCount(), th1 = ThreadCount();
Console.WriteLine($"\nprocess end: fds {fd0}->{fd1}, threads {th0}->{th1} (of which ThreadPool.ThreadCount={ThreadPool.ThreadCount}); open TCP sockets: {Run("/usr/sbin/lsof", $"-a -p {Environment.ProcessId} -iTCP -n -P").output.Split('\n').Skip(1).Count()}");
Console.WriteLine("\n================ SUMMARY ================");
foreach (var (id, name, o, sec) in Results.All) Console.WriteLine($"{o.Status,-8} {id,-5} {name}");
return Results.All.Any(r => r.o.Status == Status.FAIL) ? 1 : 0;

// ---------------------------------------------------------------- helpers
static async Task<int> RecursiveDeleteAsync(SftpClient s, string path, CancellationToken ct)
{
    // NOTE: never use path-based SftpClient.DeleteFile*/Delete*/RenameFile* on entries that may be symlinks:
    // SSH.NET canonicalizes (realpath) the path first and would act on the link TARGET (see Q7g).
    // ISftpFile methods from the listing (lstat attributes, raw FullName) act on the entry itself.
    int n = 0;
    var entries = new List<Renci.SshNet.Sftp.ISftpFile>();
    await foreach (var e in s.ListDirectoryAsync(path, ct)) if (e.Name is not "." and not "..") entries.Add(e);
    foreach (var e in entries)
    {
        if (e.IsDirectory && !e.IsSymbolicLink) n += await RecursiveDeleteAsync(s, e.FullName, ct);
        else { await e.DeleteAsync(ct); n++; }
    }
    await s.DeleteDirectoryAsync(path, ct);
    return n + 1;
}

sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
