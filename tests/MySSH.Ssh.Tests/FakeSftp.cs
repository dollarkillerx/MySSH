namespace MySSH.Ssh.Tests;

/// <summary>In-memory SFTP server with lstat semantics and hooks to slow down or fail transfers.</summary>
sealed class FakeSftp : ISftpSession
{
    public sealed class Node
    {
        public SftpEntryKind Kind;
        public byte[] Data = [];
        public string? LinkTarget;
    }

    readonly Dictionary<string, Node> _nodes = new() { ["/"] = new Node { Kind = SftpEntryKind.Directory } };
    readonly Lock _lock = new();
    int _active;

    public string HomeDirectory => "/home/u";
    public int ChunkSize { get; set; } = 16 * 1024;
    /// <summary>Called before each chunk (path, offset). Can delay, throw, or gate.</summary>
    public Func<string, long, CancellationToken, Task>? OnChunk { get; set; }
    public int MaxConcurrentTransfers { get; private set; }

    public FakeSftp() { MkdirP("/home/u"); }

    public string Full(string p) => SftpPath.Normalize(p, HomeDirectory);

    public void MkdirP(string path)
    {
        var full = Full(path);
        var cur = "";
        foreach (var seg in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur += "/" + seg;
            lock (_lock) _nodes.TryAdd(cur, new Node { Kind = SftpEntryKind.Directory });
        }
    }

    public void PutFile(string path, byte[] data) { lock (_lock) _nodes[Full(path)] = new Node { Kind = SftpEntryKind.File, Data = data }; }
    public void PutLink(string path, string target) { lock (_lock) _nodes[Full(path)] = new Node { Kind = SftpEntryKind.Symlink, LinkTarget = Full(target) }; }
    public Node? Get(string path) { lock (_lock) return _nodes.GetValueOrDefault(Full(path)); }
    public bool Exists(string path) => Get(path) is not null;

    SftpEntry ToEntry(string full, Node n) => new(SftpPath.GetName(full), full, n.Kind, n.Data.Length, DateTimeOffset.UnixEpoch,
        n.Kind == SftpEntryKind.Directory ? "drwxr-xr-x" : n.Kind == SftpEntryKind.Symlink ? "lrwxrwxrwx" : "-rw-r--r--",
        n.LinkTarget is { } t && _nodes.ContainsKey(t) ? t : null);

    Node RequireDir(string full)
    {
        var n = _nodes.GetValueOrDefault(full) ?? throw new FileNotFoundException(full);
        return n.Kind == SftpEntryKind.Directory ? n : throw new IOException($"{full} is not a directory");
    }

    public Task<string> RealPathAsync(string path, CancellationToken ct = default) => Task.FromResult(Full(path));

    public Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            RequireDir(full);
            var prefix = full == "/" ? "/" : full + "/";
            IReadOnlyList<SftpEntry> list = [.. _nodes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.Length > prefix.Length && !kv.Key[prefix.Length..].Contains('/'))
                .Select(kv => ToEntry(kv.Key, kv.Value)).OrderBy(e => e.Name, StringComparer.Ordinal)];
            return Task.FromResult(list);
        }
    }

    public Task<SftpEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock) return Task.FromResult(_nodes.TryGetValue(full, out var n) ? ToEntry(full, n) : null);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        lock (_lock)
        {
            RequireDir(SftpPath.GetParent(full));
            if (_nodes.ContainsKey(full)) throw new IOException($"{full} exists");
            _nodes[full] = new Node { Kind = SftpEntryKind.Directory };
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SftpEntry entry, bool recursive = false, CancellationToken ct = default)
    {
        var full = Full(entry.FullPath);
        lock (_lock)
        {
            var n = _nodes.GetValueOrDefault(full) ?? throw new FileNotFoundException(full);
            var children = _nodes.Keys.Where(k => k.StartsWith(full + "/", StringComparison.Ordinal)).ToList();
            if (n.Kind == SftpEntryKind.Directory && children.Count > 0 && !recursive) throw new IOException("not empty");
            if (n.Kind == SftpEntryKind.Directory) foreach (var c in children) _nodes.Remove(c);
            _nodes.Remove(full);
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(SftpEntry entry, string newPath, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var from = Full(entry.FullPath);
            var n = _nodes[from];
            _nodes.Remove(from);
            _nodes[Full(newPath)] = n;
        }
        return Task.CompletedTask;
    }

    Node Follow(string full)
    {
        for (int i = 0; i < 10; i++)
        {
            var n = _nodes.GetValueOrDefault(full) ?? throw new FileNotFoundException(full);
            if (n.Kind != SftpEntryKind.Symlink) return n;
            full = n.LinkTarget!;
        }
        throw new IOException("loop");
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<Stream>(new MemoryStream(Follow(Full(path)).Data, false));
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> ReadAllTextAsync(string path, long maxBytes = 1048576, CancellationToken ct = default) => throw new NotSupportedException();

    public Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default) => throw new NotSupportedException();

    void Enter()
    {
        lock (_lock) MaxConcurrentTransfers = Math.Max(MaxConcurrentTransfers, ++_active);
    }

    void Leave() { lock (_lock) _active--; }

    public async Task UploadAsync(Stream source, string remotePath, bool overwrite, Action<long>? progress = null, CancellationToken ct = default)
    {
        var full = Full(remotePath);
        Node node;
        lock (_lock)
        {
            RequireDir(SftpPath.GetParent(full));
            if (_nodes.TryGetValue(full, out var existing))
            {
                if (!overwrite) throw new IOException($"{full} exists");
                node = Follow(full); // like SSH.NET: writing to a link writes its target
                node.Data = [];
            }
            else
            {
                _nodes[full] = node = new Node { Kind = SftpEntryKind.File };
            }
        }
        Enter();
        try
        {
            var buf = new byte[ChunkSize];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buf, ct)) > 0)
            {
                if (OnChunk is { } hook) await hook(full, total, ct);
                ct.ThrowIfCancellationRequested();
                lock (_lock) node.Data = [.. node.Data, .. buf.AsSpan(0, n)];
                total += n;
                progress?.Invoke(total);
            }
        }
        finally { Leave(); }
    }

    public async Task DownloadAsync(string remotePath, Stream destination, Action<long>? progress = null, CancellationToken ct = default)
    {
        var full = Full(remotePath);
        byte[] data;
        lock (_lock) data = Follow(full).Data;
        Enter();
        try
        {
            long total = 0;
            while (total < data.Length)
            {
                if (OnChunk is { } hook) await hook(full, total, ct);
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize, data.Length - total);
                await destination.WriteAsync(data.AsMemory((int)total, n), ct);
                total += n;
                progress?.Invoke(total);
            }
        }
        finally { Leave(); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
