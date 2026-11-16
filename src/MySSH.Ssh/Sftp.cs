using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MySSH.Ssh;

public enum SftpEntryKind { File = 0, Directory = 1, Symlink = 2, Other = 3 }

/// <summary>A remote directory entry with lstat semantics (a symlink is reported as <see cref="SftpEntryKind.Symlink"/>, never as its target).</summary>
/// <param name="Permissions">ls-style, e.g. "drwxr-xr-x".</param>
/// <param name="LinkTarget">For symlinks: the resolved target path when it exists, else null (dangling link or unknown).</param>
public sealed record SftpEntry(
    string Name, string FullPath, SftpEntryKind Kind, long Size, DateTimeOffset Modified, string Permissions, string? LinkTarget = null);

/// <summary>
/// SFTP operations. Errors surface as .NET exceptions: <see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/>,
/// <see cref="IOException"/>; a dropped connection as <see cref="SshConnectionLostException"/>.
/// Delete/rename take an <see cref="SftpEntry"/> and act on that entry itself: they NEVER follow symlinks (P0 Q7g: SSH.NET's
/// path-based Delete*/Rename* resolve links first and hit the target).
/// </summary>
public interface ISftpSession : IAsyncDisposable
{
    /// <summary>Absolute remote home directory (initial working directory). Relative paths are resolved against it.</summary>
    string HomeDirectory { get; }

    /// <summary>Canonical absolute path (symlinks resolved where the server can).</summary>
    Task<string> RealPathAsync(string path, CancellationToken ct = default);

    /// <summary>Entries of a directory (without "." and ".."), lstat semantics, sorted by name.</summary>
    Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>lstat of one path; null when it does not exist.</summary>
    Task<SftpEntry?> StatAsync(string path, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes the entry itself (a symlink is unlinked, its target kept). Recursive delete never descends into symlinks.</summary>
    Task DeleteAsync(SftpEntry entry, bool recursive = false, CancellationToken ct = default);

    /// <summary>Renames/moves the entry itself (a symlink is renamed, not its target).</summary>
    Task RenameAsync(SftpEntry entry, string newPath, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <param name="overwrite">false = fail with <see cref="IOException"/> when the file exists.</param>
    Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default);

    /// <summary>Reads a text file; throws <see cref="InvalidDataException"/> when it is larger than <paramref name="maxBytes"/>.</summary>
    Task<string> ReadAllTextAsync(string path, long maxBytes = 1024 * 1024, CancellationToken ct = default);

    /// <summary>Creates or truncates the file and writes <paramref name="contents"/>.</summary>
    Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default);

    /// <summary>Pipelined upload. <paramref name="progress"/> receives the total bytes written so far (synchronously, on an I/O thread).</summary>
    Task UploadAsync(Stream source, string remotePath, bool overwrite, Action<long>? progress = null, CancellationToken ct = default);

    /// <summary>Pipelined download. <paramref name="progress"/> receives the total bytes read so far.</summary>
    Task DownloadAsync(string remotePath, Stream destination, Action<long>? progress = null, CancellationToken ct = default);
}

/// <summary>POSIX path helpers for remote paths.</summary>
public static class SftpPath
{
    public static string Combine(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith('/')) return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    /// <summary>Absolute, lexically normalized path ("." and ".." collapsed, duplicate slashes removed).</summary>
    public static string Normalize(string path, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = path.StartsWith('/') ? path : Combine(baseDirectory, path);
        var parts = new List<string>();
        foreach (var seg in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    public static string GetName(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>Parent of an absolute normalized path ("/" for top-level entries and for "/").</summary>
    public static string GetParent(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    public static string FormatPermissions(SftpEntryKind kind, bool isSocket, bool isChar, bool isBlock, bool isPipe,
        bool ur, bool uw, bool ux, bool gr, bool gw, bool gx, bool or, bool ow, bool ox)
    {
        char t = kind switch
        {
            SftpEntryKind.Directory => 'd',
            SftpEntryKind.Symlink => 'l',
            SftpEntryKind.File => '-',
            _ => isSocket ? 's' : isChar ? 'c' : isBlock ? 'b' : isPipe ? 'p' : '?',
        };
        Span<char> s = [t, ur ? 'r' : '-', uw ? 'w' : '-', ux ? 'x' : '-', gr ? 'r' : '-', gw ? 'w' : '-', gx ? 'x' : '-', or ? 'r' : '-', ow ? 'w' : '-', ox ? 'x' : '-'];
        return new string(s);
    }
}

internal sealed class SshSftpSession : ISftpSession
{
    readonly PoolRef<SshChain> _ref;
    readonly SftpClient _client;
    readonly IDisposable _owner;
    int _disposed;

    SshSftpSession(PoolRef<SshChain> poolRef, SftpClient client, IDisposable owner)
    {
        _ref = poolRef;
        _client = client;
        _owner = owner;
        HomeDirectory = client.WorkingDirectory;
    }

    public static async Task<ISftpSession> OpenAsync(PoolRef<SshChain> poolRef, CancellationToken ct)
    {
        var chain = poolRef.Chain;
        var (client, owner) = await chain.ConnectExtraTargetClientAsync(ci => new SftpClient(ci)
        {
            OperationTimeout = chain.Options.OperationTimeout,
        }, ct);
        return new SshSftpSession(poolRef, client, owner);
    }

    public string HomeDirectory { get; }

    SshChain Chain => _ref.Chain;

    string Full(string path) => SftpPath.Normalize(path, HomeDirectory);

    async Task<T> Guard<T>(string path, Func<Task<T>> op)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var r = await op();
            Chain.MarkActivity();
            return r;
        }
        catch (SftpPathNotFoundException ex) { throw new FileNotFoundException($"No such file or directory: {path}", path, ex); }
        catch (SftpPermissionDeniedException ex) { throw new UnauthorizedAccessException($"Permission denied: {path}", ex); }
        catch (SshConnectionException ex) { throw new SshConnectionLostException($"SFTP connection lost: {ex.Message}", ex); }
        catch (SshOperationTimeoutException ex) { throw new TimeoutException($"SFTP operation on {path} timed out.", ex); }
        catch (SshException ex) when (!_client.IsConnected) { throw new SshConnectionLostException($"SFTP connection lost: {ex.Message}", ex); }
        catch (SshException ex) { throw new IOException($"SFTP error on {path}: {ex.Message}", ex); }
    }

    async Task Guard(string path, Func<Task> op) => await Guard(path, async () => { await op(); return true; });

    internal static SftpEntryKind KindOf(ISftpFile f) =>
        f.IsSymbolicLink ? SftpEntryKind.Symlink : f.IsDirectory ? SftpEntryKind.Directory : f.IsRegularFile ? SftpEntryKind.File : SftpEntryKind.Other;

    static SftpEntry Map(ISftpFile f, string? linkTarget = null)
    {
        var kind = KindOf(f);
        var perms = SftpPath.FormatPermissions(kind, f.IsSocket, f.IsCharacterDevice, f.IsBlockDevice, f.IsNamedPipe,
            f.OwnerCanRead, f.OwnerCanWrite, f.OwnerCanExecute, f.GroupCanRead, f.GroupCanWrite, f.GroupCanExecute,
            f.OthersCanRead, f.OthersCanWrite, f.OthersCanExecute);
        var modified = new DateTimeOffset(DateTime.SpecifyKind(f.LastWriteTimeUtc, DateTimeKind.Utc));
        return new SftpEntry(f.Name, f.FullName, kind, f.Length, modified, perms, linkTarget);
    }

    async Task<string?> LinkTargetAsync(string full, CancellationToken ct)
    {
        try
        {
            var target = await _client.GetAsync(full, ct);
            // SSH.NET falls back to realpath(parent)+name when realpath fails (dangling link): that is the link itself.
            return target.FullName == full ? null : target.FullName;
        }
        catch (SshException) { return null; }
    }

    /// <summary>The entry itself (lstat), never its symlink target. Null when missing.</summary>
    async Task<ISftpFile?> ResolveAsync(string full, CancellationToken ct)
    {
        if (full == "/") return await _client.GetAsync("/", ct);
        try
        {
            var f = await _client.GetAsync(full, ct);
            // GetAsync realpaths first: equal path ⇒ no link on the way and f is the entry itself.
            if (f.FullName == full) return f;
        }
        catch (SftpPathNotFoundException) { }
        var parent = SftpPath.GetParent(full);
        var name = SftpPath.GetName(full);
        try
        {
            await foreach (var e in _client.ListDirectoryAsync(parent, ct))
                if (e.Name == name) return e;
        }
        catch (SftpPathNotFoundException) { }
        return null;
    }

    async Task<ISftpFile> RequireAsync(SftpEntry entry, CancellationToken ct)
    {
        var full = Full(entry.FullPath);
        var f = await ResolveAsync(full, ct) ?? throw new SftpPathNotFoundException($"{full} does not exist.");
        if (KindOf(f) != entry.Kind)
            throw new IOException($"{full} changed since it was listed (was {entry.Kind}, now {KindOf(f)}); refusing to act on it.");
        return f;
    }

    public Task<string> RealPathAsync(string path, CancellationToken ct = default)
        => Guard(path, async () => (await _client.GetAsync(Full(path), ct)).FullName);

    public Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken ct = default) => Guard(path, async () =>
    {
        var files = new List<ISftpFile>();
        await foreach (var f in _client.ListDirectoryAsync(Full(path), ct))
            if (f.Name is not "." and not "..") files.Add(f);
        var entries = new SftpEntry[files.Count];
        using var throttle = new SemaphoreSlim(8);
        await Task.WhenAll(files.Select(async (f, i) =>
        {
            string? target = null;
            if (f.IsSymbolicLink)
            {
                await throttle.WaitAsync(ct);
                try { target = await LinkTargetAsync(f.FullName, ct); }
                finally { throttle.Release(); }
            }
            entries[i] = Map(f, target);
        }));
        Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (IReadOnlyList<SftpEntry>)entries;
    });

    public Task<SftpEntry?> StatAsync(string path, CancellationToken ct = default) => Guard(path, async () =>
    {
        var full = Full(path);
        var f = await ResolveAsync(full, ct);
        if (f is null) return null;
        return Map(f, f.IsSymbolicLink ? await LinkTargetAsync(f.FullName, ct) : null);
    });

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => Guard(path, () => _client.CreateDirectoryAsync(Full(path), ct));

    public Task DeleteAsync(SftpEntry entry, bool recursive = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Guard(entry.FullPath, async () =>
        {
            var f = await RequireAsync(entry, ct);
            if (f.IsDirectory && !f.IsSymbolicLink && recursive) await DeleteTreeAsync(f, ct);
            else await f.DeleteAsync(ct); // ISftpFile: rmdir/remove on the entry's own path, no realpath
        });
    }

    async Task DeleteTreeAsync(ISftpFile dir, CancellationToken ct)
    {
        var children = new List<ISftpFile>();
        await foreach (var c in _client.ListDirectoryAsync(dir.FullName, ct))
            if (c.Name is not "." and not "..") children.Add(c);
        foreach (var c in children)
        {
            ct.ThrowIfCancellationRequested();
            if (c.IsDirectory && !c.IsSymbolicLink) await DeleteTreeAsync(c, ct);
            else await c.DeleteAsync(ct);
        }
        await dir.DeleteAsync(ct);
    }

    public Task RenameAsync(SftpEntry entry, string newPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(newPath);
        return Guard(entry.FullPath, async () =>
        {
            var f = await RequireAsync(entry, ct);
            var dest = Full(newPath);
            await Task.Run(() => f.MoveTo(dest), ct); // sync-only API; SSH_FXP_RENAME on the entry's own path
        });
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => Guard(path, async () => (Stream)await _client.OpenAsync(Full(path), FileMode.Open, FileAccess.Read, ct));

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct = default)
        => Guard(path, async () =>
        {
            var full = Full(path);
            if (!overwrite && await ResolveAsync(full, ct) is not null) throw new IOException($"{full} already exists.");
            return (Stream)await _client.OpenAsync(full, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, ct);
        });

    public Task<string> ReadAllTextAsync(string path, long maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        return Guard(path, async () =>
        {
            await using var s = await _client.OpenAsync(Full(path), FileMode.Open, FileAccess.Read, ct);
            var bytes = await ReadCappedAsync(s, maxBytes, path, ct);
            return DecodeText(bytes, Chain.Encoding);
        });
    }

    internal static async Task<byte[]> ReadCappedAsync(Stream s, long maxBytes, string path, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[32 * 1024];
        while (true)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n == 0) break;
            if (ms.Length + n > maxBytes) throw new InvalidDataException($"{path} is larger than {maxBytes} bytes.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    internal static string DecodeText(byte[] bytes, Encoding encoding)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(span[3..]);
        return encoding.GetString(span);
    }

    public Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return Guard(path, async () =>
        {
            await using var s = await _client.OpenAsync(Full(path), FileMode.Create, FileAccess.Write, ct);
            await s.WriteAsync(Chain.Encoding.GetBytes(contents), ct);
        });
    }

    public Task UploadAsync(Stream source, string remotePath, bool overwrite, Action<long>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Guard(remotePath, () => _client.UploadFileAsync(source, Full(remotePath), overwrite,
            progress is null ? null : new SyncProgress<UploadFileProgressReport>(r => progress((long)r.TotalBytesUploaded)), ct));
    }

    public Task DownloadAsync(string remotePath, Stream destination, Action<long>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Guard(remotePath, () => _client.DownloadFileAsync(Full(remotePath), destination,
            progress is null ? null : new SyncProgress<DownloadFileProgressReport>(r => progress((long)r.TotalBytesDownloaded)), ct));
    }

    /// <summary>Synchronous IProgress (System.Progress posts to the thread pool and may report after completion, P0 Q7).</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Task.Run(_owner.Dispose);
        await _ref.DisposeAsync();
    }
}
