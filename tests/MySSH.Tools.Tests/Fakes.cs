using System.Collections.Concurrent;
using System.Text;
using MySSH.Core;
using MySSH.Ssh;

namespace MySSH.Tools.Tests;

internal static class T
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly ToolActor Mcp = new(ConnectionOrigin.Mcp, "mcp:test-client", "Test client (MCP)");
    public static readonly ToolActor Ai = new(ConnectionOrigin.Ai, "ai:claude/opus", "Claude (AI)");

    public static System.Text.Json.JsonElement Json(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static System.Text.Json.JsonElement Args(params (string Name, object? Value)[] args)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (n, v) in args)
            {
                switch (v)
                {
                    case null: w.WriteNull(n); break;
                    case string s: w.WriteString(n, s); break;
                    case bool b: w.WriteBoolean(n, b); break;
                    case int i: w.WriteNumber(n, i); break;
                    case long l: w.WriteNumber(n, l); break;
                    case double d: w.WriteNumber(n, d); break;
                    default: throw new ArgumentException(v.GetType().Name);
                }
            }
            w.WriteEndObject();
        }
        return Json(Encoding.UTF8.GetString(ms.ToArray()));
    }
}

internal sealed class MemHosts : IHostRepository
{
    private readonly ConcurrentDictionary<Guid, Host> _d = new();
    public Host Add(Host h) { _d[h.Id] = h; return h; }
    public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Host>>([.. _d.Values.OrderBy(h => h.Label, StringComparer.OrdinalIgnoreCase)]);
    public Task<Host?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(id));
    public Task SaveAsync(Host host, CancellationToken ct = default) { _d[host.Id] = host; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct = default) { _d.TryRemove(id, out _); return Task.CompletedTask; }
}

internal sealed class MemAudit : IAuditLogStore
{
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();
    public AuditEntry Single() => Assert.Single(Entries);
    public Task AppendAsync(AuditEntry entry, CancellationToken ct = default) { Entries.Enqueue(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>([.. Entries.OrderByDescending(e => e.Timestamp).Take(query.Limit)]);
    public Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class FakeApproval : IApprovalService
{
    public ApprovalOutcome Outcome { get; set; } = ApprovalOutcome.Approved;
    /// <summary>Delay before answering (honours the token unless <see cref="IgnoreToken"/>).</summary>
    public TimeSpan Delay { get; set; }
    /// <summary>Never answer, never look at the token (a broken UI).</summary>
    public bool Hang { get; set; }
    public bool IgnoreToken { get; set; }
    public ConcurrentQueue<ApprovalRequest> Requests { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        Started.TrySetResult();
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
        }
        if (Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(Delay, IgnoreToken ? CancellationToken.None : ct);
            }
            catch (OperationCanceledException)
            {
                return ApprovalOutcome.Cancelled;
            }
        }
        return Outcome;
    }
}

internal sealed class FakeTerminals : ITerminalSessionRegistry
{
    public List<TerminalSessionInfo> Sessions { get; } = [];
    public Dictionary<string, string> Screens { get; } = [];
    public ConcurrentQueue<(string Id, string Text, bool Execute)> Sent { get; } = new();
    public IReadOnlyList<TerminalSessionInfo> List() => [.. Sessions];
    public Task<string?> ReadAsync(string sessionId, int lines, CancellationToken ct = default)
    {
        if (!Screens.TryGetValue(sessionId, out var s)) return Task.FromResult<string?>(null);
        var all = s.Split('\n');
        return Task.FromResult<string?>(string.Join('\n', all.Skip(Math.Max(0, all.Length - lines))));
    }
    public Task<bool> SendAsync(string sessionId, string text, bool execute, CancellationToken ct = default)
    {
        var s = Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null || !s.IsConnected) return Task.FromResult(false);
        Sent.Enqueue((sessionId, text, execute));
        return Task.FromResult(true);
    }
}

/// <summary>Connector returning in-memory connections; records every connect (host, origin).</summary>
internal sealed class FakeConnector(IHostRepository hosts) : ISshConnector
{
    public ConcurrentQueue<(Guid HostId, ConnectionOrigin Origin)> Connects { get; } = new();
    public ConcurrentDictionary<Guid, MemSftp> Sftp { get; } = new();
    public Func<string, ExecOptions, CancellationToken, Task<ExecResult>> Exec { get; set; } =
        (cmd, _, _) => Task.FromResult(new ExecResult(0, "ok\n", "", false, false, false, false));
    public SshConnectException? ConnectError { get; set; }
    public ConcurrentQueue<(string Command, ExecOptions Options)> Commands { get; } = new();
    public int OpenConnections;

    public MemSftp SftpFor(Guid hostId) => Sftp.GetOrAdd(hostId, _ => new MemSftp());

    public async Task<ISshConnection> ConnectAsync(Guid hostId, ConnectionOrigin origin, CancellationToken ct = default)
    {
        Connects.Enqueue((hostId, origin));
        ct.ThrowIfCancellationRequested();
        if (ConnectError is { } e) throw e;
        var host = await hosts.GetAsync(hostId, ct) ?? throw new SshConnectException(SshFailureKind.InvalidConfiguration, "no such host");
        Interlocked.Increment(ref OpenConnections);
        return new FakeConnection(this, host, origin);
    }

    private sealed class FakeConnection(FakeConnector owner, Host host, ConnectionOrigin origin) : ISshConnection
    {
        private int _disposed;
        public Host Host => host;
        public ConnectionOrigin Origin => origin;
        public SshConnectionState State => SshConnectionState.Connected;
        public event Action<SshConnectionStateChange>? StateChanged { add { } remove { } }
        public Task<ITerminalBackend> OpenShellAsync(int columns, int rows, string term = "xterm-256color", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExecResult> ExecAsync(string command, ExecOptions? options = null, CancellationToken ct = default)
        {
            owner.Commands.Enqueue((command, options ?? new ExecOptions()));
            return owner.Exec(command, options ?? new ExecOptions(), ct);
        }
        public Task<ISftpSession> OpenSftpAsync(CancellationToken ct = default) => Task.FromResult<ISftpSession>(owner.SftpFor(host.Id));
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner.OpenConnections);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>In-memory SFTP with lstat semantics, writes supported. Disposing is a no-op (shared per host).</summary>
internal sealed class MemSftp : ISftpSession
{
    private readonly Dictionary<string, (SftpEntryKind Kind, byte[] Data, string? Link)> _nodes = new(StringComparer.Ordinal)
    {
        ["/"] = (SftpEntryKind.Directory, [], null),
    };
    private readonly Lock _lock = new();

    public MemSftp() => Mkdir("/home/u");

    public string HomeDirectory => "/home/u";
    public string Full(string p) => SftpPath.Normalize(p, HomeDirectory);

    public void Mkdir(string path)
    {
        var cur = "";
        foreach (var seg in Full(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur += "/" + seg;
            lock (_lock) _nodes.TryAdd(cur, (SftpEntryKind.Directory, [], null));
        }
    }

    public void Put(string path, byte[] data)
    {
        Mkdir(SftpPath.GetParent(Full(path)));
        lock (_lock) _nodes[Full(path)] = (SftpEntryKind.File, data, null);
    }
    public void Put(string path, string text) => Put(path, Encoding.UTF8.GetBytes(text));
    public void Link(string path, string target)
    {
        Mkdir(SftpPath.GetParent(Full(path)));
        lock (_lock) _nodes[Full(path)] = (SftpEntryKind.Symlink, [], Full(target));
    }
    public byte[]? Read(string path) { lock (_lock) return _nodes.TryGetValue(Full(path), out var n) ? n.Data : null; }
    public bool Exists(string path) { lock (_lock) return _nodes.ContainsKey(Full(path)); }

    private SftpEntry Entry(string full, (SftpEntryKind Kind, byte[] Data, string? Link) n) =>
        new(SftpPath.GetName(full), full, n.Kind, n.Data.Length, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            n.Kind switch { SftpEntryKind.Directory => "drwxr-xr-x", SftpEntryKind.Symlink => "lrwxrwxrwx", _ => "-rw-r--r--" }, n.Link);

    public Task<string> RealPathAsync(string path, CancellationToken ct = default) => Task.FromResult(Full(path));

    public Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            if (!_nodes.TryGetValue(full, out var d)) throw new FileNotFoundException(full);
            if (d.Kind != SftpEntryKind.Directory) throw new IOException($"{full} is not a directory");
            var prefix = full == "/" ? "/" : full + "/";
            IReadOnlyList<SftpEntry> list = [.. _nodes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.Length > prefix.Length && !kv.Key[prefix.Length..].Contains('/'))
                .Select(kv => Entry(kv.Key, kv.Value)).OrderBy(e => e.Name, StringComparer.Ordinal)];
            return Task.FromResult(list);
        }
    }

    public Task<SftpEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock) return Task.FromResult(_nodes.TryGetValue(full, out var n) ? Entry(full, n) : null);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            if (!_nodes.TryGetValue(SftpPath.GetParent(full), out var p) || p.Kind != SftpEntryKind.Directory) throw new FileNotFoundException(SftpPath.GetParent(full));
            if (_nodes.ContainsKey(full)) throw new IOException($"{full} exists");
            _nodes[full] = (SftpEntryKind.Directory, [], null);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SftpEntry entry, bool recursive = false, CancellationToken ct = default)
    {
        var full = Full(entry.FullPath);
        lock (_lock)
        {
            if (!_nodes.TryGetValue(full, out var n)) throw new FileNotFoundException(full);
            var children = _nodes.Keys.Where(k => k.StartsWith(full + "/", StringComparison.Ordinal)).ToList();
            if (n.Kind == SftpEntryKind.Directory && children.Count > 0 && !recursive) throw new IOException("Directory not empty");
            foreach (var c in children) _nodes.Remove(c);
            _nodes.Remove(full);
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(SftpEntry entry, string newPath, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            for (var i = 0; i < 10; i++)
            {
                if (!_nodes.TryGetValue(full, out var n)) throw new FileNotFoundException(full);
                if (n.Kind == SftpEntryKind.Symlink) { full = n.Link!; continue; }
                return Task.FromResult<Stream>(new MemoryStream(n.Data, false));
            }
        }
        throw new IOException("symlink loop");
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            if (!overwrite && _nodes.ContainsKey(full)) throw new IOException($"{full} already exists.");
            if (!_nodes.TryGetValue(SftpPath.GetParent(full), out var p) || p.Kind != SftpEntryKind.Directory) throw new FileNotFoundException(SftpPath.GetParent(full));
        }
        return Task.FromResult<Stream>(new CommitStream(bytes => { lock (_lock) _nodes[full] = (SftpEntryKind.File, bytes, null); }));
    }

    private sealed class CommitStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _done;
        protected override void Dispose(bool disposing)
        {
            if (!_done) { _done = true; commit(ToArray()); }
            base.Dispose(disposing);
        }
    }

    public Task<string> ReadAllTextAsync(string path, long maxBytes = 1048576, CancellationToken ct = default) => throw new NotSupportedException();
    public Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default) => throw new NotSupportedException();
    public Task UploadAsync(Stream source, string remotePath, bool overwrite, Action<long>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DownloadAsync(string remotePath, Stream destination, Action<long>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Manual clock: timers fire only when <see cref="Advance"/> moves past their due time.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }
    public override long GetTimestamp() { lock (_lock) return _ticks; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            _ticks += by.Ticks;
            due = [.. _timers.Where(t => t.Due is { } d && d <= _ticks)];
        }
        foreach (var t in due) t.Fire();
    }

    public int ActiveTimers { get { lock (_lock) return _timers.Count(t => t.Due is not null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
            }
            return true;
        }

        public void Fire()
        {
            lock (owner._lock)
                Due = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? owner._ticks + _period.Ticks : null;
            callback(state);
        }

        public void Dispose() { lock (owner._lock) { Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Everything an invoker test needs.</summary>
internal sealed class Rig
{
    public Rig(ToolOptions? options = null, TimeProvider? time = null)
    {
        Options = options ?? new ToolOptions();
        Connector = new FakeConnector(Hosts);
        Time = time ?? TimeProvider.System;
        Layer = ToolServices.Create(Hosts, Connector, Approval, Audit, Terminals, Options, Time);
    }

    public MemHosts Hosts { get; } = new();
    public FakeApproval Approval { get; } = new();
    public MemAudit Audit { get; } = new();
    public FakeTerminals Terminals { get; } = new();
    public FakeConnector Connector { get; }
    public ToolOptions Options { get; }
    public TimeProvider Time { get; }
    public ToolLayer Layer { get; }
    public ToolInvoker Invoker => Layer.Invoker;

    public Host AddHost(string label, AiPolicy policy, IReadOnlyList<string>? allowlist = null, string hostname = "10.0.0.1") =>
        Hosts.Add(new Host { Label = label, Hostname = hostname, Username = "deploy", AiPolicy = policy, AiCommandAllowlist = allowlist ?? [], Tags = ["prod"] });

    public TerminalSessionInfo AddSession(Host host, string id = "s1", bool connected = true, string screen = "line1\nline2\n$ ")
    {
        var s = new TerminalSessionInfo(id, host.Id, host.Label, $"{host.Label} #1", connected, 80, 24);
        Terminals.Sessions.Add(s);
        Terminals.Screens[id] = screen;
        return s;
    }

    public Task<ToolCallResult> CallAsync(string tool, System.Text.Json.JsonElement args, ToolActor? actor = null, CancellationToken? ct = null) =>
        Invoker.InvokeAsync(new ToolCallRequest(tool, args), actor ?? T.Mcp, ct ?? T.Ct);

    public Task<ToolCallResult> CallAsync(string tool, params (string, object?)[] args) => CallAsync(tool, T.Args(args));
}
