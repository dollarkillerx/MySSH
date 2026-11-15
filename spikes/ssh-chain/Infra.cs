using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshChainSpike;

// ---------------------------------------------------------------- environment
static class Env
{
    public static readonly string ProjectDir = FindProjectDir();
    public static readonly string Work = Path.Combine(ProjectDir, ".work");
    public static readonly string Keys = Path.Combine(Work, "keys");
    public static readonly string User = Environment.UserName;
    public const string Passphrase = "spike-pass";
    public const string Loopback = "127.0.0.1";
    public const int Bastion = 2222, Target = 2223, Hop2 = 2224, Socks = 1080;

    static string FindProjectDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SshChainSpike.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("project dir not found");
    }

    public static string Key(string name) => Path.Combine(Keys, name);

    public static (int code, string output) Run(string file, string args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = ProjectDir,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(true); return (-1, "timeout"); }
        return (p.ExitCode, (so.Result + se.Result).Trim());
    }

    public static int OpenFdCount() => Directory.GetFileSystemEntries("/dev/fd").Length;
    public static int ThreadCount() { using var p = Process.GetCurrentProcess(); return p.Threads.Count; }

    public static string SshdLog(string inst) => Path.Combine(Work, inst, "sshd.log");
    public static long LogSize(string inst) => new FileInfo(SshdLog(inst)).Length;
    /// <summary>Text appended to an sshd log since the given offset.</summary>
    public static string LogSince(string inst, long offset)
    {
        using var fs = new FileStream(SshdLog(inst), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        return new StreamReader(fs).ReadToEnd();
    }
}

// ---------------------------------------------------------------- auth
static class Auth
{
    /// <summary>Production rule: empty passphrase == no passphrase.</summary>
    public static string? NormalizePassphrase(string? p) => string.IsNullOrEmpty(p) ? null : p;

    /// <summary>Load key from bytes (production keys come decrypted from the DB, not from a path).</summary>
    public static PrivateKeyFile LoadKey(string path, string? passphrase, bool normalize = true)
    {
        var bytes = File.ReadAllBytes(path);
        return new PrivateKeyFile(new MemoryStream(bytes), normalize ? NormalizePassphrase(passphrase) : passphrase);
    }

    public static AuthenticationMethod[] Key(string user, string keyName, string? passphrase = null)
        => [new PrivateKeyAuthenticationMethod(user, LoadKey(Env.Key(keyName), passphrase))];
}

// ---------------------------------------------------------------- TOFU
enum TofuPolicy { AcceptNew, Strict }
enum TofuVerdict { Match, AcceptedNew, RejectedUnknown, RejectedMismatch }

/// <summary>known_hosts-style store keyed by the REAL host:port (never the forwarded loopback port).</summary>
sealed class TofuStore
{
    // hostKey ("[host]:port" or "host") -> keyType -> base64 blob
    readonly Dictionary<string, Dictionary<string, string>> _entries = new();
    public readonly List<string> Events = new();
    public readonly string FilePath;

    public TofuStore(string filePath) { FilePath = filePath; if (File.Exists(filePath)) File.Delete(filePath); }

    public static string HostKey(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    /// <summary>OpenSSH format: "SHA256:" + unpadded base64(sha256(blob)).</summary>
    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    public IReadOnlyCollection<string> Hosts => _entries.Keys;
    public bool TryGet(string host, int port, out Dictionary<string, string>? keys) => _entries.TryGetValue(HostKey(host, port), out keys);

    public EventHandler<HostKeyEventArgs> Handler(string realHost, int realPort, TofuPolicy policy, Action<TofuVerdict, string>? onVerdict = null)
        => (_, e) =>
        {
            var hk = HostKey(realHost, realPort);
            var b64 = Convert.ToBase64String(e.HostKey);
            var fp = Fingerprint(e.HostKey);
            TofuVerdict v;
            lock (_entries)
            {
                if (_entries.TryGetValue(hk, out var known))
                {
                    if (known.TryGetValue(e.HostKeyName, out var stored))
                        v = stored == b64 ? TofuVerdict.Match : TofuVerdict.RejectedMismatch;
                    else
                        // known host, different key type: treat as mismatch (OpenSSH would warn); production should
                        // reorder HostKeyAlgorithms to prefer the stored type before connecting (see Prefer()).
                        v = TofuVerdict.RejectedMismatch;
                }
                else if (policy == TofuPolicy.AcceptNew)
                {
                    _entries[hk] = new() { [e.HostKeyName] = b64 };
                    File.AppendAllText(FilePath, $"{hk} {e.HostKeyName} {b64}\n");
                    v = TofuVerdict.AcceptedNew;
                }
                else v = TofuVerdict.RejectedUnknown;
            }
            e.CanTrust = v is TofuVerdict.Match or TofuVerdict.AcceptedNew;
            var msg = $"{hk} {e.HostKeyName} {fp} (lib FingerPrintSHA256={e.FingerPrintSHA256}) -> {v}";
            lock (Events) Events.Add(msg);
            onVerdict?.Invoke(v, fp);
        };

    /// <summary>Move host key algorithms we already have stored for this host to the front.</summary>
    public void Prefer(ConnectionInfo ci, string realHost, int realPort)
    {
        if (!TryGet(realHost, realPort, out var known) || known is null) return;
        foreach (var type in known.Keys)
            if (ci.HostKeyAlgorithms.ContainsKey(type)) ci.HostKeyAlgorithms.SetPosition(type, 0);
    }
}

// ---------------------------------------------------------------- chain factory (prototype of IConnectionFactory)
sealed record Hop(string Name, string Host, int Port, string User, Func<AuthenticationMethod[]> Auth);
sealed record ProxySpec(ProxyTypes Type, string Host, int Port, string? User = null, string? Password = null);

sealed class ChainConnection<T> : IDisposable where T : BaseClient
{
    public readonly List<SshClient> Jumps = new();
    public readonly List<ForwardedPortLocal> Forwards = new();
    public T Target = null!;
    public readonly List<(string hop, string endpoint, double ms)> Timings = new();

    public void Dispose()
    {
        try { Target?.Dispose(); } catch { }
        for (int i = Forwards.Count - 1; i >= 0; i--) { try { Forwards[i].Stop(); Forwards[i].Dispose(); } catch { } }
        for (int i = Jumps.Count - 1; i >= 0; i--) { try { Jumps[i].Dispose(); } catch { } }
    }
}

static class ChainFactory
{
    public static TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static ConnectionInfo MakeInfo(string host, int port, Hop hop, ProxySpec? proxy)
    {
        var ci = proxy is null
            ? new ConnectionInfo(host, port, hop.User, hop.Auth())
            : new ConnectionInfo(host, port, hop.User, proxy.Type, proxy.Host, proxy.Port, proxy.User!, proxy.Password!, hop.Auth());
        ci.Timeout = Timeout;
        return ci;
    }

    /// <summary>[proxy] -> hops[0] -> ... -> hops[^1]. Each subsequent hop goes through ForwardedPortLocal("127.0.0.1", 0, ...)
    /// on the previous hop. Host keys are checked against the REAL host:port of each hop.</summary>
    public static async Task<ChainConnection<T>> ConnectAsync<T>(IReadOnlyList<Hop> hops, ProxySpec? proxy, TofuStore tofu,
        TofuPolicy policy, Func<ConnectionInfo, T> make, CancellationToken ct = default, Action<ConnectionInfo>? tweak = null)
        where T : BaseClient
    {
        var chain = new ChainConnection<T>();
        try
        {
            SshClient? prev = null;
            for (int i = 0; i < hops.Count; i++)
            {
                var hop = hops[i];
                string host = hop.Host; int port = hop.Port;
                if (prev != null)
                {
                    var fwd = new ForwardedPortLocal(Env.Loopback, 0, hop.Host, (uint)hop.Port);
                    prev.AddForwardedPort(fwd);
                    fwd.Start();
                    chain.Forwards.Add(fwd);
                    host = Env.Loopback; port = checked((int)fwd.BoundPort);
                }
                var ci = MakeInfo(host, port, hop, i == 0 ? proxy : null);
                tofu.Prefer(ci, hop.Host, hop.Port);
                tweak?.Invoke(ci);
                BaseClient client = i == hops.Count - 1 ? make(ci) : new SshClient(ci);
                client.HostKeyReceived += tofu.Handler(hop.Host, hop.Port, policy);
                var sw = Stopwatch.StartNew();
                try { await client.ConnectAsync(ct); }
                catch { client.Dispose(); throw; }
                chain.Timings.Add((hop.Name, $"{host}:{port}", sw.Elapsed.TotalMilliseconds));
                if (i == hops.Count - 1) chain.Target = (T)client;
                else { prev = (SshClient)client; chain.Jumps.Add(prev); }
            }
            return chain;
        }
        catch { chain.Dispose(); throw; }
    }
}

// ---------------------------------------------------------------- reflection helpers (things SSH.NET does not expose)
static class Internals
{
    public static Session GetSession(BaseClient c)
        => (Session)typeof(BaseClient).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;

    public static uint ShellChannelNumber(ShellStream s)
    {
        var ch = typeof(ShellStream).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
        // IChannel.LocalChannelNumber is on an internal interface; search implemented interfaces.
        foreach (var it in ch.GetType().GetInterfaces())
        {
            var p = it.GetProperty("LocalChannelNumber");
            if (p != null) return (uint)p.GetValue(ch)!;
        }
        throw new InvalidOperationException("LocalChannelNumber not found");
    }
}

// ---------------------------------------------------------------- fallback: single-accept direct-tcpip bridge
/// <summary>Own loopback listener that accepts exactly ONE connection, closes the listener, and pumps the accepted
/// socket through an SSH.NET-internal direct-tcpip channel (reflection: ISession.CreateChannelDirectTcpip,
/// IChannelDirectTcpip.Open/Bind). Removes the "any local process can use the forward" window of ForwardedPortLocal.</summary>
sealed class SingleAcceptBridge : IForwardedPort
{
    public event EventHandler? Closing;
    readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    public int Port { get; }
    public Task Pump { get; }
    public string? AcceptedFrom;
    System.Net.Sockets.Socket? _sock;

    public SingleAcceptBridge(SshClient jump, string host, int port)
    {
        _listener.Start(1);
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        var session = typeof(BaseClient).GetProperty("Session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(jump)!;
        var iSession = typeof(BaseClient).Assembly.GetType("Renci.SshNet.ISession")!;
        var iChan = typeof(BaseClient).Assembly.GetType("Renci.SshNet.Channels.IChannelDirectTcpip")!;
        Pump = Task.Factory.StartNew(() =>
        {
            _sock = _listener.AcceptSocket();
            AcceptedFrom = _sock.RemoteEndPoint?.ToString();
            _listener.Stop();                                   // nobody else can connect from now on
            var ch = iSession.GetMethod("CreateChannelDirectTcpip")!.Invoke(session, null)!;
            iChan.GetMethod("Open")!.Invoke(ch, [host, (uint)port, this, _sock]);
            iChan.GetMethod("Bind")!.Invoke(ch, null);         // blocks: socket -> channel pump until EOF/close
            ((IDisposable)ch).Dispose();
        }, TaskCreationOptions.LongRunning);
    }

    public void Dispose()
    {
        Closing?.Invoke(this, EventArgs.Empty);
        try { _listener.Stop(); } catch { }
        try { _sock?.Dispose(); } catch { }
    }
}

// ---------------------------------------------------------------- tiny ILoggerFactory to observe SSH.NET internals
sealed class CaptureLoggerFactory : ILoggerFactory
{
    public readonly List<string> Lines = new();
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
    public void Dispose() { }
    sealed class L(CaptureLoggerFactory f, string cat) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        { lock (f.Lines) f.Lines.Add($"{DateTime.Now:HH:mm:ss.fff} {level} {cat}: {fmt(state, ex)}"); }
    }
}

// ---------------------------------------------------------------- result table
enum Status { PASS, FAIL, PARTIAL, INFO }
sealed record Outcome(Status Status, string Evidence);

static class Results
{
    public static readonly List<(string id, string name, Outcome o, double sec)> All = new();

    public static string Filter = "";
    public static async Task Run(string id, string name, Func<Task<Outcome>> body)
    {
        if (Filter.Length > 2 && !id.StartsWith(Filter, StringComparison.OrdinalIgnoreCase)) return;
        Console.WriteLine($"\n--- {id} {name}");
        var sw = Stopwatch.StartNew();
        Outcome o;
        try { o = await body(); }
        catch (Exception ex) { o = new Outcome(Status.FAIL, $"unexpected {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Program"))}"); }
        All.Add((id, name, o, sw.Elapsed.TotalSeconds));
        Console.WriteLine($"[{o.Status}] {id} {name} ({sw.Elapsed.TotalSeconds:F2}s)\n    {o.Evidence.Replace("\n", "\n    ")}");
    }

    public static Outcome Ok(bool cond, string evidence, Status ifFalse = Status.FAIL) => new(cond ? Status.PASS : ifFalse, evidence);
}
