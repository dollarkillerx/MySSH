using System.Diagnostics;
using System.Threading.Channels;

namespace MySSH.Ssh;

public enum TransferDirection { Upload = 0, Download = 1 }

public enum TransferState { Queued = 0, Running = 1, Completed = 2, Failed = 3, Cancelled = 4 }

public enum ConflictPolicy
{
    Overwrite = 0,
    Skip = 1,
    /// <summary>Keep both: the new file becomes "name (1).ext".</summary>
    Rename = 2,
    /// <summary>Ask <see cref="TransferQueueOptions.OnConflict"/> per file (skips when no callback is set).</summary>
    Ask = 3,
}

public enum ConflictResolution { Overwrite = 0, Skip = 1, Rename = 2, CancelItem = 3 }

/// <param name="DestinationIsSymlink">The existing destination is a symlink: Overwrite replaces the link itself, never writes through it.</param>
public sealed record TransferConflict(
    Guid ItemId, TransferDirection Direction, string SourcePath, string DestinationPath,
    long SourceSize, long? DestinationSize, bool DestinationIsSymlink);

/// <summary>Throttled progress snapshot of one queue item (directories aggregate all their files).</summary>
public sealed record TransferProgress(
    Guid ItemId, long BytesTransferred, long TotalBytes, double BytesPerSecond, string? CurrentFile, int FilesDone, int FilesTotal);

public sealed record TransferQueueOptions
{
    public int MaxConcurrency { get; init; } = 2;
    /// <summary>Minimum time between two <see cref="TransferQueue.ProgressChanged"/> events per item (UI throttling).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.Ask;
    /// <summary>Asked for each conflicting file when the policy is Ask. Called on a worker thread; may await UI.</summary>
    public Func<TransferConflict, CancellationToken, ValueTask<ConflictResolution>>? OnConflict { get; init; }
}

/// <summary>One upload or download (file or directory tree). Properties are updated from worker threads.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The owning TransferQueue disposes the token source.")]
public sealed class TransferItem
{
    readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly CancellationTokenSource Cts = new();
    internal readonly Lock ProgressLock = new();
    internal long LastEmitTimestamp;
    internal long LastEmitBytes;
    internal double Speed;
    long _bytes;
    int _state;

    internal TransferItem(TransferDirection direction, string source, string destination)
    {
        Direction = direction;
        SourcePath = source;
        DestinationPath = destination;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public TransferDirection Direction { get; }
    /// <summary>Local path for uploads, remote path for downloads.</summary>
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public bool IsDirectory { get; internal set; }
    public TransferState State => (TransferState)Volatile.Read(ref _state);
    public long BytesTransferred => Interlocked.Read(ref _bytes);
    public long TotalBytes { get; internal set; }
    public double BytesPerSecond => Volatile.Read(ref Speed);
    public int FilesTotal { get; internal set; }
    public int FilesDone { get; internal set; }
    public int FilesSkipped { get; internal set; }
    public string? CurrentFile { get; internal set; }
    public string? Error { get; internal set; }
    /// <summary>Completes (never faults) when the item reaches a terminal state.</summary>
    public Task Completion => _done.Task;

    internal void AddBytes(long delta) => Interlocked.Add(ref _bytes, delta);
    internal bool TrySetState(TransferState from, TransferState to) => Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
    internal void SetTerminal(TransferState s)
    {
        Volatile.Write(ref _state, (int)s);
        _done.TrySetResult();
    }
}

/// <summary>
/// UI-agnostic SFTP transfer queue (design §8): FIFO with bounded concurrency, throttled thread-safe progress, per-item and
/// global cancel, conflict policy, partial-file cleanup on cancel/failure. Remote symlinks are never followed: a directory
/// download skips them, a directory upload refuses to write into a symlinked remote directory, and Overwrite of a symlinked
/// destination replaces the link itself.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It is a queue; name fixed by the design.")]
public sealed class TransferQueue : IAsyncDisposable
{
    readonly ISftpSession _sftp;
    readonly TransferQueueOptions _options;
    readonly Channel<TransferItem> _queue = Channel.CreateUnbounded<TransferItem>();
    readonly List<TransferItem> _items = [];
    readonly Lock _lock = new();
    readonly Task[] _workers;

    public TransferQueue(ISftpSession sftp, TransferQueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sftp);
        _sftp = sftp;
        _options = options ?? new TransferQueueOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConcurrency);
        _workers = [.. Enumerable.Range(0, _options.MaxConcurrency).Select(_ => Task.Run(WorkerAsync))];
    }

    /// <summary>Raised on a worker thread when an item changes state.</summary>
    public event Action<TransferItem>? ItemChanged;

    /// <summary>Raised on a worker/I-O thread, at most once per <see cref="TransferQueueOptions.ProgressInterval"/> per item (plus a final one).</summary>
    public event Action<TransferProgress>? ProgressChanged;

    public IReadOnlyList<TransferItem> Items
    {
        get { lock (_lock) return [.. _items]; }
    }

    /// <param name="remotePath">Destination file path, or the destination directory when <paramref name="localPath"/> is a directory.</param>
    public TransferItem EnqueueUpload(string localPath, string remotePath)
        => Enqueue(new TransferItem(TransferDirection.Upload, Path.GetFullPath(localPath), remotePath));

    /// <param name="localPath">Destination file path, or the destination directory when the remote path is a directory.</param>
    public TransferItem EnqueueDownload(string remotePath, string localPath)
        => Enqueue(new TransferItem(TransferDirection.Download, remotePath, Path.GetFullPath(localPath)));

    TransferItem Enqueue(TransferItem item)
    {
        lock (_lock) _items.Add(item);
        ObjectDisposedException.ThrowIf(!_queue.Writer.TryWrite(item), this);
        Notify(item);
        return item;
    }

    public bool Cancel(Guid itemId)
    {
        TransferItem? item;
        lock (_lock) item = _items.Find(i => i.Id == itemId);
        if (item is null) return false;
        return CancelItem(item);
    }

    bool CancelItem(TransferItem item)
    {
        if (item.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled) return false;
        try { item.Cts.Cancel(); } catch (ObjectDisposedException) { }
        if (item.TrySetState(TransferState.Queued, TransferState.Cancelled))
        {
            item.SetTerminal(TransferState.Cancelled);
            Notify(item);
        }
        return true;
    }

    public void CancelAll()
    {
        foreach (var i in Items) CancelItem(i);
    }

    /// <summary>Completes when every item enqueued so far has finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(Items.Select(i => i.Completion));

    void Notify(TransferItem item)
    {
        try { ItemChanged?.Invoke(item); }
#pragma warning disable CA1031 // subscriber failures must not break the queue
        catch (Exception) { }
#pragma warning restore CA1031
    }

    void Report(TransferItem item, bool force)
    {
        TransferProgress p;
        lock (item.ProgressLock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(item.LastEmitTimestamp, now);
            if (!force && elapsed < _options.ProgressInterval) return;
            var bytes = item.BytesTransferred;
            if (elapsed > TimeSpan.Zero && item.LastEmitTimestamp != 0)
            {
                var instant = (bytes - item.LastEmitBytes) / elapsed.TotalSeconds;
                item.Speed = item.Speed <= 0 ? instant : (item.Speed * 0.7) + (instant * 0.3);
            }
            item.LastEmitTimestamp = now;
            item.LastEmitBytes = bytes;
            p = new TransferProgress(item.Id, bytes, item.TotalBytes, item.Speed, item.CurrentFile, item.FilesDone, item.FilesTotal);
        }
        try { ProgressChanged?.Invoke(p); }
#pragma warning disable CA1031
        catch (Exception) { }
#pragma warning restore CA1031
    }

    async Task WorkerAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            if (!item.TrySetState(TransferState.Queued, TransferState.Running)) continue; // cancelled while queued
            Notify(item);
            item.LastEmitTimestamp = Stopwatch.GetTimestamp();
            TransferState final;
            try
            {
                if (item.Direction == TransferDirection.Upload) await UploadItemAsync(item, item.Cts.Token);
                else await DownloadItemAsync(item, item.Cts.Token);
                final = TransferState.Completed;
            }
            catch (OperationCanceledException) when (item.Cts.IsCancellationRequested)
            {
                final = TransferState.Cancelled;
            }
#pragma warning disable CA1031 // every failure is recorded on the item
            catch (Exception ex)
            {
                item.Error = ex.Message;
                final = TransferState.Failed;
            }
#pragma warning restore CA1031
            Report(item, force: true);
            item.SetTerminal(final);
            Notify(item);
        }
    }

    // ------------------------------------------------------------------ upload

    sealed record FileJob(string Source, string Destination, long Size);

    async Task UploadItemAsync(TransferItem item, CancellationToken ct)
    {
        var src = item.SourcePath;
        var jobs = new List<FileJob>();
        var dirs = new List<string>();
        if (Directory.Exists(src))
        {
            item.IsDirectory = true;
            dirs.Add(item.DestinationPath);
            ScanLocal(src, item.DestinationPath, jobs, dirs, ct);
        }
        else if (File.Exists(src))
        {
            jobs.Add(new FileJob(src, item.DestinationPath, new FileInfo(src).Length));
        }
        else
        {
            throw new FileNotFoundException($"{src} does not exist.", src);
        }
        item.TotalBytes = jobs.Sum(j => j.Size);
        item.FilesTotal = jobs.Count;
        Report(item, force: true);

        foreach (var d in dirs) await EnsureRemoteDirectoryAsync(d, ct);
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            await UploadFileAsync(item, job, ct);
        }
    }

    static void ScanLocal(string dir, string remoteDir, List<FileJob> jobs, List<string> dirs, CancellationToken ct)
    {
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var remote = SftpPath.Combine(remoteDir, entry.Name);
            if (entry is DirectoryInfo sub)
            {
                if (sub.LinkTarget is not null) continue; // never follow local directory links (loops)
                dirs.Add(remote);
                ScanLocal(sub.FullName, remote, jobs, dirs, ct);
            }
            else if (entry is FileInfo f)
            {
                long length;
                if (f.LinkTarget is null) length = f.Length;
                else
                {
                    // a local file symlink uploads its target's content; dangling links are skipped
                    try
                    {
                        using var h = File.OpenHandle(f.FullName);
                        length = RandomAccess.GetLength(h);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                }
                jobs.Add(new FileJob(f.FullName, remote, length));
            }
        }
    }

    async Task EnsureRemoteDirectoryAsync(string path, CancellationToken ct)
    {
        var existing = await _sftp.StatAsync(path, ct);
        if (existing is null) { await _sftp.CreateDirectoryAsync(path, ct); return; }
        if (existing.Kind == SftpEntryKind.Directory) return;
        throw new IOException(existing.Kind == SftpEntryKind.Symlink
            ? $"Remote {path} is a symbolic link; refusing to upload through it."
            : $"Remote {path} exists and is not a directory.");
    }

    async Task UploadFileAsync(TransferItem item, FileJob job, CancellationToken ct)
    {
        item.CurrentFile = job.Source;
        var dest = job.Destination;
        var existing = await _sftp.StatAsync(dest, ct);
        bool overwrite = false;
        if (existing is not null)
        {
            if (existing.Kind == SftpEntryKind.Directory) throw new IOException($"Remote {dest} is a directory.");
            var resolution = await ResolveConflictAsync(item, job.Source, dest, job.Size, existing.Kind == SftpEntryKind.Symlink ? null : existing.Size,
                existing.Kind == SftpEntryKind.Symlink, ct);
            switch (resolution)
            {
                case ConflictResolution.Skip:
                    Skip(item, job);
                    return;
                case ConflictResolution.Rename:
                    dest = await UniqueRemoteNameAsync(dest, ct);
                    break;
                default:
                    if (existing.Kind == SftpEntryKind.Symlink) await _sftp.DeleteAsync(existing, false, ct); // replace the link, not its target
                    else overwrite = true;
                    break;
            }
        }

        long baseBytes = item.BytesTransferred;
        long last = 0;
        bool started = false;
        try
        {
            await using var fs = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            started = true;
            await _sftp.UploadAsync(fs, dest, overwrite, total =>
            {
                item.AddBytes(total - Interlocked.Exchange(ref last, total));
                Report(item, force: false);
            }, ct);
        }
        catch when (started)
        {
            item.AddBytes(-(item.BytesTransferred - baseBytes));
            await CleanupRemoteAsync(dest);
            throw;
        }
        item.AddBytes(job.Size - (item.BytesTransferred - baseBytes)); // reconcile (file may have changed size)
        item.FilesDone++;
        Report(item, force: false);
    }

    async Task CleanupRemoteAsync(string path)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (await _sftp.StatAsync(path, cts.Token) is { Kind: SftpEntryKind.File } e)
                await _sftp.DeleteAsync(e, false, cts.Token);
        }
#pragma warning disable CA1031 // best effort; the original failure is what the user sees
        catch (Exception) { }
#pragma warning restore CA1031
    }

    async Task<string> UniqueRemoteNameAsync(string path, CancellationToken ct)
    {
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = SftpPath.Combine(SftpPath.GetParent(path), NumberedName(SftpPath.GetName(path), i));
            if (await _sftp.StatAsync(candidate, ct) is null) return candidate;
        }
        throw new IOException($"No free name for {path}.");
    }

    internal static string NumberedName(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }

    // ------------------------------------------------------------------ download

    async Task DownloadItemAsync(TransferItem item, CancellationToken ct)
    {
        var root = await _sftp.StatAsync(item.SourcePath, ct)
            ?? throw new FileNotFoundException($"Remote {item.SourcePath} does not exist.", item.SourcePath);
        if (root.Kind == SftpEntryKind.Symlink)
        {
            // An explicitly selected link to a file downloads the file's content; links to directories are not followed.
            var target = root.LinkTarget is null ? null : await _sftp.StatAsync(root.LinkTarget, ct);
            if (target is not { Kind: SftpEntryKind.File })
                throw new IOException($"Remote {item.SourcePath} is a symbolic link to {(target is null ? "nothing" : "a directory")}; open its target instead.");
            root = root with { Size = target.Size };
        }

        var jobs = new List<FileJob>();
        var dirs = new List<string>();
        int skippedLinks = 0;
        if (root.Kind == SftpEntryKind.Directory)
        {
            item.IsDirectory = true;
            dirs.Add(item.DestinationPath);
            skippedLinks = await ScanRemoteAsync(root.FullPath, item.DestinationPath, jobs, dirs, ct);
        }
        else
        {
            jobs.Add(new FileJob(root.FullPath, item.DestinationPath, root.Size));
        }
        item.FilesSkipped += skippedLinks;
        item.TotalBytes = jobs.Sum(j => j.Size);
        item.FilesTotal = jobs.Count;
        Report(item, force: true);

        foreach (var d in dirs)
        {
            var st = LocalStat(d);
            if (st.Exists && (!st.IsDirectory || st.IsLink))
                throw new IOException($"Local {d} exists and is not a plain directory.");
            Directory.CreateDirectory(d);
        }
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            await DownloadFileAsync(item, job, ct);
        }
    }

    async Task<int> ScanRemoteAsync(string remoteDir, string localDir, List<FileJob> jobs, List<string> dirs, CancellationToken ct)
    {
        int skipped = 0;
        foreach (var e in await _sftp.ListAsync(remoteDir, ct))
        {
            ct.ThrowIfCancellationRequested();
            var local = Path.Combine(localDir, e.Name);
            switch (e.Kind)
            {
                case SftpEntryKind.Directory:
                    dirs.Add(local);
                    skipped += await ScanRemoteAsync(e.FullPath, local, jobs, dirs, ct);
                    break;
                case SftpEntryKind.File:
                    jobs.Add(new FileJob(e.FullPath, local, e.Size));
                    break;
                default:
                    skipped++; // symlinks and special files are never followed
                    break;
            }
        }
        return skipped;
    }

    async Task DownloadFileAsync(TransferItem item, FileJob job, CancellationToken ct)
    {
        item.CurrentFile = job.Source;
        var dest = job.Destination;
        var (exists, isLink, isDir, length) = LocalStat(dest);
        if (isDir) throw new IOException($"Local {dest} is a directory.");
        if (exists)
        {
            var resolution = await ResolveConflictAsync(item, job.Source, dest, job.Size, length, isLink, ct);
            switch (resolution)
            {
                case ConflictResolution.Skip:
                    Skip(item, job);
                    return;
                case ConflictResolution.Rename:
                    dest = UniqueLocalName(dest);
                    break;
                default:
                    File.Delete(dest); // removes a link itself, never its target
                    break;
            }
        }

        long baseBytes = item.BytesTransferred;
        long last = 0;
        bool created = false;
        try
        {
            await using var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            created = true;
            await _sftp.DownloadAsync(job.Source, fs, total =>
            {
                item.AddBytes(total - Interlocked.Exchange(ref last, total));
                Report(item, force: false);
            }, ct);
        }
        catch when (created)
        {
            item.AddBytes(-(item.BytesTransferred - baseBytes));
            try { File.Delete(dest); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        item.AddBytes(job.Size - (item.BytesTransferred - baseBytes));
        item.FilesDone++;
        Report(item, force: false);
    }

    /// <summary>lstat-like view of a local path (a dangling link exists and is a link).</summary>
    internal static (bool Exists, bool IsLink, bool IsDirectory, long? Length) LocalStat(string path)
    {
        string? linkTarget;
        try { linkTarget = new FileInfo(path).LinkTarget; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { linkTarget = null; }
        if (linkTarget is not null) return (true, true, false, null);
        var fi = new FileInfo(path);
        if (fi.Exists) return (true, false, false, fi.Length);
        if (Directory.Exists(path)) return (true, false, true, null);
        return (false, false, false, null);
    }

    static string UniqueLocalName(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, NumberedName(name, i));
            if (!LocalStat(candidate).Exists) return candidate;
        }
        throw new IOException($"No free name for {path}.");
    }

    // ------------------------------------------------------------------ shared

    void Skip(TransferItem item, FileJob job)
    {
        item.FilesSkipped++;
        item.TotalBytes -= job.Size;
        Report(item, force: false);
    }

    async Task<ConflictResolution> ResolveConflictAsync(TransferItem item, string source, string dest, long sourceSize, long? destSize, bool destIsLink, CancellationToken ct)
    {
        var r = _options.ConflictPolicy switch
        {
            ConflictPolicy.Overwrite => ConflictResolution.Overwrite,
            ConflictPolicy.Skip => ConflictResolution.Skip,
            ConflictPolicy.Rename => ConflictResolution.Rename,
            _ => _options.OnConflict is { } ask
                ? await ask(new TransferConflict(item.Id, item.Direction, source, dest, sourceSize, destSize, destIsLink), ct)
                : ConflictResolution.Skip,
        };
        if (r == ConflictResolution.CancelItem)
        {
            await item.Cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        CancelAll();
        await Task.WhenAll(_workers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (var i in Items) i.Cts.Dispose();
    }
}
