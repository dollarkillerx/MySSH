using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace MySSH.Ssh.IntegrationTests;

/// <summary>
/// C# port of spikes/ssh-chain/env.sh: NON-ROOT sshd instances on 127.0.0.1 (free ports) plus an OpenSSH SOCKS5 proxy
/// (<c>ssh -N -D</c> through the bastion). Everything lives in a fresh temp dir; nothing outside it is touched.
/// Pubkey auth only (a non-root sshd cannot do password auth). When /usr/sbin/sshd is missing (Windows CI) every test
/// skips with <see cref="SkipReason"/>.
/// </summary>
public sealed class SshdEnvironment : IAsyncLifetime
{
    public const string Passphrase = "it-pass";
    const string Sshd = "/usr/sbin/sshd";
    const string Ssh = "/usr/bin/ssh";
    const string SshKeygen = "/usr/bin/ssh-keygen";

    readonly Dictionary<string, Instance> _instances = [];
    Process? _socks;
    bool _perSourcePenalties = true;

    public string? SkipReason { get; private set; }
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "myssh-it-" + Guid.NewGuid().ToString("N")[..10]);
    public string User { get; } = Environment.UserName;
    public string KeyPlain => File.ReadAllText(Path.Combine(Root, "keys", "id_ed25519"));
    public string KeyEncrypted => File.ReadAllText(Path.Combine(Root, "keys", "id_ed25519_pass"));
    public string KeyRsaPemEncrypted => File.ReadAllText(Path.Combine(Root, "keys", "id_rsa_pem_pass"));
    public int SocksPort { get; private set; }
    /// <summary>Scratch directory shared with the remote side (same machine): SFTP tests create fixtures here.</summary>
    public string Data => Path.Combine(Root, "data");

    public sealed class Instance(string name, string dir, int port)
    {
        public string Name { get; } = name;
        public string Dir { get; } = dir;
        public int Port { get; } = port;
        public Process? Process { get; set; }
        public string Log => Path.Combine(Dir, "sshd.log");
        /// <summary>Waits until the log text written after <paramref name="offset"/> matches (sshd children log asynchronously).</summary>
        public async Task AssertLogAsync(long offset, string regex)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var text = ReadLog()[(int)offset..];
                if (System.Text.RegularExpressions.Regex.IsMatch(text, regex)) return;
                if (sw.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail($"{Name} log has no match for '{regex}':\n{text}");
                await Task.Delay(100);
            }
        }

        public string ReadLog()
        {
            using var fs = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(fs).ReadToEnd();
        }
    }

    public Instance this[string name] => _instances[name];

    public void RequireSshd() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    public async ValueTask InitializeAsync()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists(Sshd) || !File.Exists(SshKeygen) || !File.Exists(Ssh))
        {
            SkipReason = $"OpenSSH server not available ({Sshd}); integration tests need a local non-root sshd.";
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "keys"));
            Directory.CreateDirectory(Data);
            await KeygenAsync("ed25519", "", Path.Combine(Root, "keys", "id_ed25519"));
            await KeygenAsync("ed25519", Passphrase, Path.Combine(Root, "keys", "id_ed25519_pass"));
            await KeygenAsync("rsa", Passphrase, Path.Combine(Root, "keys", "id_rsa_pem_pass"), "-m", "PEM", "-b", "2048");
            File.WriteAllText(Path.Combine(Root, "authorized_keys"), string.Concat(
                new[] { "id_ed25519", "id_ed25519_pass", "id_rsa_pem_pass" }.Select(k => File.ReadAllText(Path.Combine(Root, "keys", k + ".pub")))));
            await Task.WhenAll(StartInstanceAsync("bastion"), StartInstanceAsync("target"), StartInstanceAsync("hop2"));
            await StartSocksAsync();
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            SkipReason = $"Could not start the local sshd test environment: {ex.Message}";
            await DisposeAsync();
        }
#pragma warning restore CA1031
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    static async Task KeygenAsync(string type, string pass, string path, params string[] extra)
    {
        var args = new List<string> { "-q", "-t", type, "-N", pass, "-C", "myssh-it", "-f", path };
        args.AddRange(extra);
        var (code, output) = await RunAsync(SshKeygen, args);
        if (code != 0) throw new InvalidOperationException($"ssh-keygen failed: {output}");
    }

    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); return (-1, "timeout"); }
        return (p.ExitCode, (await so + await se).Trim());
    }

    /// <summary>Starts (or restarts with the same port and keys) an sshd instance.</summary>
    public async Task<Instance> StartInstanceAsync(string name, int? port = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        Instance inst;
        lock (_instances)
        {
            inst = _instances.TryGetValue(name, out var existing) ? existing : new Instance(name, dir, port ?? FreePort());
            _instances[name] = inst;
        }
        var hostKey = Path.Combine(dir, "ssh_host_ed25519_key");
        if (!File.Exists(hostKey)) await KeygenAsync("ed25519", "", hostKey);
        var cfg = Path.Combine(dir, "sshd_config");
        string Config(bool penalties) => $"""
            Port {inst.Port}
            ListenAddress 127.0.0.1
            HostKey {hostKey}
            PidFile {Path.Combine(dir, "sshd.pid")}
            AuthorizedKeysFile {Path.Combine(Root, "authorized_keys")}
            PubkeyAuthentication yes
            PasswordAuthentication no
            KbdInteractiveAuthentication no
            UsePAM no
            StrictModes no
            AllowTcpForwarding yes
            PermitTTY yes
            AllowUsers {User}
            LogLevel DEBUG1
            Subsystem sftp internal-sftp
            ClientAliveInterval 0
            MaxStartups 100:30:200
            MaxSessions 50
            {(penalties ? "PerSourcePenalties no" : "")}

            """;
        File.WriteAllText(cfg, Config(_perSourcePenalties));
        var (code, output) = await RunAsync(Sshd, ["-t", "-f", cfg]);
        if (code != 0 && _perSourcePenalties && output.Contains("PerSourcePenalties", StringComparison.Ordinal))
        {
            _perSourcePenalties = false; // OpenSSH < 9.8: option unknown and not needed
            File.WriteAllText(cfg, Config(false));
            (code, output) = await RunAsync(Sshd, ["-t", "-f", cfg]);
        }
        if (code != 0) throw new InvalidOperationException($"sshd -t failed for {name}: {output}");

        var psi = new ProcessStartInfo(Sshd) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-D", "-e", "-f", cfg }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var log = new StreamWriter(new FileStream(inst.Log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        inst.Process = p;
        await WaitForBannerAsync(inst.Port, p);
        return inst;
    }

    static async Task WaitForBannerAsync(int port, Process? p)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (p is { HasExited: true }) throw new InvalidOperationException($"process exited with {p.ExitCode}");
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                var buf = new byte[64];
                using var cts = new CancellationTokenSource(2000);
                int n = await c.GetStream().ReadAsync(buf, cts.Token);
                if (n > 0 && Encoding.ASCII.GetString(buf, 0, n).StartsWith("SSH-", StringComparison.Ordinal)) return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException($"nothing answers on 127.0.0.1:{port}");
    }

    async Task StartSocksAsync()
    {
        SocksPort = FreePort();
        var psi = new ProcessStartInfo(Ssh) { RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in new[]
        {
            "-N", "-D", $"127.0.0.1:{SocksPort}", "-p", this["bastion"].Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", Path.Combine(Root, "keys", "id_ed25519"), "-F", "/dev/null", "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
            "-o", $"UserKnownHostsFile={Path.Combine(Root, "kh")}", "-o", "StrictHostKeyChecking=accept-new", "-o", "ExitOnForwardFailure=yes",
            $"{User}@127.0.0.1",
        }) psi.ArgumentList.Add(a);
        _socks = Process.Start(psi)!;
        _socks.StandardInput.Close();
        _socks.BeginErrorReadLine();
        _socks.BeginOutputReadLine();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (_socks.HasExited) throw new InvalidOperationException("ssh -D exited");
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, SocksPort);
                return;
            }
            catch (SocketException) when (sw.Elapsed < TimeSpan.FromSeconds(15)) { await Task.Delay(100); }
        }
    }

    /// <summary>The process and all its descendants (sshd-session children retitle themselves, so -f matching does not find them).</summary>
    static async Task<List<int>> ProcessTreeAsync(int root)
    {
        var (_, output) = await RunAsync("/bin/ps", ["-axo", "pid=,ppid="]);
        var children = new Dictionary<int, List<int>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                (children.TryGetValue(ppid, out var l) ? l : children[ppid] = []).Add(pid);
        }
        var result = new List<int> { root };
        for (int i = 0; i < result.Count; i++)
            if (children.TryGetValue(result[i], out var kids)) result.AddRange(kids);
        return result;
    }

    static async Task SignalTreeAsync(int root, string signal, bool includeRoot = true)
    {
        var pids = await ProcessTreeAsync(root);
        if (!includeRoot) pids.Remove(root);
        if (pids.Count == 0) return;
        if (signal == "KILL") pids.Reverse();
        await RunAsync("/bin/kill", [$"-{signal}", .. pids.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
    }

    /// <summary>Signals an instance's listener AND its per-connection sshd-session children.</summary>
    public async Task KillInstanceAsync(string name, string signal = "KILL")
    {
        var inst = this[name];
        // STOP/CONT only the per-connection sshd-session processes: stopping a DIRECT child of the test host makes the
        // .NET runtime's SIGCHLD handling wedge the test process. The listener keeps accepting, the sessions freeze.
        if (inst.Process is { HasExited: false } running) await SignalTreeAsync(running.Id, signal, includeRoot: signal == "KILL");
        if (signal == "KILL" && inst.Process is { } p)
        {
            try { await p.WaitForExitAsync(new CancellationTokenSource(5000).Token); } catch (OperationCanceledException) { }
            p.Dispose();
            inst.Process = null;
        }
    }

    /// <summary>Stops an instance, replaces its host key and starts it again on the same port.</summary>
    public async Task RegenerateHostKeyAsync(string name)
    {
        await KillInstanceAsync(name);
        foreach (var f in Directory.GetFiles(this[name].Dir, "ssh_host_*")) File.Delete(f);
        await StartInstanceAsync(name);
    }

    public async ValueTask DisposeAsync()
    {
        try { _socks?.Kill(true); } catch (InvalidOperationException) { }
        _socks?.Dispose();
        foreach (var i in _instances.Values)
        {
            if (i.Process is { HasExited: false } p)
            {
                await SignalTreeAsync(p.Id, "CONT");
                await SignalTreeAsync(p.Id, "KILL");
            }
        }
        // Safety net: anything still referencing our temp dir (sshd-session children, the SOCKS ssh, frozen processes).
        if (Directory.Exists(Root))
        {
            await RunAsync("/usr/bin/pkill", ["-CONT", "-f", Root]);
            await RunAsync("/usr/bin/pkill", ["-KILL", "-f", Root]);
            await Task.Delay(200);
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (var i in _instances.Values) i.Process?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class SshdGroup : ICollectionFixture<SshdEnvironment>
{
    public const string Name = "sshd";
}
