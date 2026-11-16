namespace MySSH.Ssh.Tests;

public sealed class TransferQueueTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    readonly string _dir = Directory.CreateTempSubdirectory("myssh-tq-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    string Local(string rel, int size = 0, int seed = 1)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(p, data);
        return p;
    }

    static async Task Done(TransferItem item) => await item.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);

    [Fact]
    public async Task Uploads_a_file_and_a_directory_tree()
    {
        var sftp = new FakeSftp();
        await using var q = new TransferQueue(sftp, new TransferQueueOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        var single = q.EnqueueUpload(Local("one.bin", 100_000), "/home/u/one.bin");
        Local("tree/a.bin", 10);
        Local("tree/sub/b.bin", 20);
        Local("tree/sub/deeper/c.bin", 30);
        var tree = q.EnqueueUpload(Path.Combine(_dir, "tree"), "/home/u/tree");
        await Done(single);
        await Done(tree);
        Assert.Equal((TransferState.Completed, 100_000L), (single.State, single.BytesTransferred));
        Assert.Equal(TransferState.Completed, tree.State);
        Assert.True(tree.IsDirectory);
        Assert.Equal((60L, 3, 3), (tree.TotalBytes, tree.FilesTotal, tree.FilesDone));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "tree/sub/deeper/c.bin")), sftp.Get("/home/u/tree/sub/deeper/c.bin")!.Data);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "one.bin")), sftp.Get("one.bin")!.Data);
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, "old", null)]
    [InlineData(ConflictPolicy.Overwrite, "new", null)]
    [InlineData(ConflictPolicy.Rename, "old", "new")]
    public async Task Conflict_policies(ConflictPolicy policy, string expectedOriginal, string? expectedRenamed)
    {
        var sftp = new FakeSftp();
        sftp.PutFile("/home/u/f.txt", "old"u8.ToArray());
        var src = Path.Combine(_dir, "f.txt");
        File.WriteAllText(src, "new");
        await using var q = new TransferQueue(sftp, new TransferQueueOptions { ConflictPolicy = policy });
        var item = q.EnqueueUpload(src, "/home/u/f.txt");
        await Done(item);
        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(expectedOriginal, System.Text.Encoding.UTF8.GetString(sftp.Get("/home/u/f.txt")!.Data));
        Assert.Equal(expectedRenamed, sftp.Get("/home/u/f (1).txt") is { } r ? System.Text.Encoding.UTF8.GetString(r.Data) : null);
        Assert.Equal(policy == ConflictPolicy.Skip ? 1 : 0, item.FilesSkipped);
    }

    [Fact]
    public async Task Ask_policy_calls_back_per_file_and_can_cancel_the_item()
    {
        var sftp = new FakeSftp();
        sftp.MkdirP("/home/u/d");
        sftp.PutFile("/home/u/d/a.bin", [1]);
        sftp.PutFile("/home/u/d/b.bin", [2]);
        Local("d/a.bin", 5);
        Local("d/b.bin", 5);
        var asked = new List<TransferConflict>();
        await using var q = new TransferQueue(sftp, new TransferQueueOptions
        {
            ConflictPolicy = ConflictPolicy.Ask,
            OnConflict = (c, _) => { lock (asked) asked.Add(c); return ValueTask.FromResult(c.DestinationPath.EndsWith("a.bin", StringComparison.Ordinal) ? ConflictResolution.Overwrite : ConflictResolution.CancelItem); },
        });
        var item = q.EnqueueUpload(Path.Combine(_dir, "d"), "/home/u/d");
        await Done(item);
        Assert.Equal(TransferState.Cancelled, item.State);
        Assert.Equal(2, asked.Count);
        Assert.Equal((5L, (long?)1L), (asked[0].SourceSize, asked[0].DestinationSize));
        Assert.Equal(5, sftp.Get("/home/u/d/a.bin")!.Data.Length);
        Assert.Equal([2], sftp.Get("/home/u/d/b.bin")!.Data); // untouched
    }

    [Fact]
    public async Task Overwriting_a_remote_symlink_replaces_the_link_not_its_target()
    {
        var sftp = new FakeSftp();
        sftp.PutFile("/etc/passwd", "root"u8.ToArray());
        sftp.PutLink("/home/u/evil", "/etc/passwd");
        var src = Path.Combine(_dir, "x");
        File.WriteAllText(src, "mine");
        var conflicts = new List<TransferConflict>();
        await using var q = new TransferQueue(sftp, new TransferQueueOptions
        {
            OnConflict = (c, _) => { conflicts.Add(c); return ValueTask.FromResult(ConflictResolution.Overwrite); },
        });
        var item = q.EnqueueUpload(src, "/home/u/evil");
        await Done(item);
        Assert.Equal(TransferState.Completed, item.State);
        Assert.True(Assert.Single(conflicts).DestinationIsSymlink);
        Assert.Equal("root"u8.ToArray(), sftp.Get("/etc/passwd")!.Data);
        Assert.Equal(FakeSftpKind.File, (FakeSftpKind)sftp.Get("/home/u/evil")!.Kind);
        Assert.Equal("mine"u8.ToArray(), sftp.Get("/home/u/evil")!.Data);
    }

    enum FakeSftpKind { File = SftpEntryKind.File }

    [Fact]
    public async Task Directory_upload_refuses_to_write_through_a_remote_symlinked_directory()
    {
        var sftp = new FakeSftp();
        sftp.MkdirP("/srv/elsewhere");
        sftp.MkdirP("/home/u/dest");
        sftp.PutLink("/home/u/dest/sub", "/srv/elsewhere");
        Local("tree/sub/file.bin", 10);
        await using var q = new TransferQueue(sftp, new TransferQueueOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        var item = q.EnqueueUpload(Path.Combine(_dir, "tree"), "/home/u/dest");
        await Done(item);
        Assert.Equal(TransferState.Failed, item.State);
        Assert.Contains("symbolic link", item.Error, StringComparison.Ordinal);
        Assert.Empty(await sftp.ListAsync("/srv/elsewhere", Ct));
    }

    [Fact]
    public async Task Directory_download_skips_symlinks_and_reproduces_the_tree()
    {
        var sftp = new FakeSftp();
        sftp.MkdirP("/home/u/r/sub");
        sftp.PutFile("/home/u/r/a", [1, 2, 3]);
        sftp.PutFile("/home/u/r/sub/b", [4]);
        sftp.MkdirP("/secret");
        sftp.PutFile("/secret/key", [9]);
        sftp.PutLink("/home/u/r/escape", "/secret");
        sftp.PutLink("/home/u/r/keylink", "/secret/key");
        await using var q = new TransferQueue(sftp);
        var dest = Path.Combine(_dir, "down");
        var item = q.EnqueueDownload("/home/u/r", dest);
        await Done(item);
        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(2, item.FilesSkipped);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(dest, "a")));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(dest, "sub", "b")));
        Assert.False(Path.Exists(Path.Combine(dest, "escape")));
        Assert.False(Path.Exists(Path.Combine(dest, "keylink")));

        // an explicitly chosen link to a file downloads the file content
        var single = q.EnqueueDownload("/home/u/r/keylink", Path.Combine(_dir, "key.copy"));
        await Done(single);
        Assert.Equal(TransferState.Completed, single.State);
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(_dir, "key.copy")));
    }

    [Fact]
    public async Task Cancelling_an_upload_deletes_the_partial_remote_file()
    {
        var sftp = new FakeSftp { ChunkSize = 1024 };
        var started = new TaskCompletionSource();
        sftp.OnChunk = async (_, offset, ct) => { if (offset > 0) started.TrySetResult(); await Task.Delay(5, ct); };
        await using var q = new TransferQueue(sftp);
        var item = q.EnqueueUpload(Local("big.bin", 512 * 1024), "/home/u/big.bin");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(sftp.Exists("/home/u/big.bin"));
        Assert.True(q.Cancel(item.Id));
        await Done(item);
        Assert.Equal(TransferState.Cancelled, item.State);
        Assert.False(sftp.Exists("/home/u/big.bin"));
        Assert.Equal(0, item.BytesTransferred);
    }

    [Fact]
    public async Task A_failing_download_deletes_the_partial_local_file_and_reports_the_error()
    {
        var sftp = new FakeSftp { ChunkSize = 1024 };
        sftp.PutFile("/home/u/f", new byte[10_000]);
        sftp.OnChunk = (_, offset, _) => offset >= 4096 ? throw new IOException("disk on fire") : Task.CompletedTask;
        await using var q = new TransferQueue(sftp);
        var dest = Path.Combine(_dir, "f");
        var item = q.EnqueueDownload("/home/u/f", dest);
        await Done(item);
        Assert.Equal(TransferState.Failed, item.State);
        Assert.Equal("disk on fire", item.Error);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Concurrency_is_bounded_queued_items_cancel_immediately_and_progress_is_throttled()
    {
        var sftp = new FakeSftp { ChunkSize = 4096 };
        sftp.OnChunk = (_, _, ct) => Task.Delay(2, ct);
        var progress = new List<TransferProgress>();
        await using var q = new TransferQueue(sftp, new TransferQueueOptions { MaxConcurrency = 2, ProgressInterval = TimeSpan.FromMilliseconds(200) });
        q.ProgressChanged += p => { lock (progress) progress.Add(p); };
        var states = new List<(Guid, TransferState)>();
        q.ItemChanged += i => { lock (states) states.Add((i.Id, i.State)); };
        var items = Enumerable.Range(0, 5).Select(i => q.EnqueueUpload(Local($"f{i}", 256 * 1024, i), $"/home/u/f{i}")).ToList();
        Assert.True(q.Cancel(items[4].Id));
        Assert.Equal(TransferState.Cancelled, items[4].State);
        await Task.WhenAll(items.Select(Done));
        Assert.All(items.Take(4), i => Assert.Equal(TransferState.Completed, i.State));
        Assert.Equal(2, sftp.MaxConcurrentTransfers);
        Assert.False(sftp.Exists("/home/u/f4"));
        lock (progress)
        {
            foreach (var g in progress.GroupBy(p => p.ItemId))
            {
                Assert.Equal(256 * 1024, g.Last().BytesTransferred); // final event always sent
                Assert.True(g.Count() < 40, $"{g.Count()} events: not throttled");
            }
        }
        lock (states) Assert.Contains((items[0].Id, TransferState.Running), states);
        Assert.False(q.Cancel(items[0].Id)); // already completed
    }

    [Fact]
    public async Task Missing_source_fails_and_cancel_all_stops_everything()
    {
        var sftp = new FakeSftp();
        var gate = new TaskCompletionSource();
        sftp.OnChunk = async (_, _, ct) => await gate.Task.WaitAsync(ct);
        await using var q = new TransferQueue(sftp, new TransferQueueOptions { MaxConcurrency = 1 });
        var missing = q.EnqueueUpload(Path.Combine(_dir, "nope"), "/home/u/nope");
        await Done(missing);
        Assert.Equal(TransferState.Failed, missing.State);
        var a = q.EnqueueUpload(Local("a", 10), "/home/u/a");
        var b = q.EnqueueUpload(Local("b", 10), "/home/u/b");
        await TestWait.UntilAsync(() => a.State == TransferState.Running);
        q.CancelAll();
        await Done(a);
        await Done(b);
        Assert.Equal((TransferState.Cancelled, TransferState.Cancelled), (a.State, b.State));
        Assert.False(sftp.Exists("/home/u/a"));
    }

    [Fact]
    public void Numbered_names_keep_the_extension()
    {
        Assert.Equal("report (1).pdf", TransferQueue.NumberedName("report.pdf", 1));
        Assert.Equal("Makefile (2)", TransferQueue.NumberedName("Makefile", 2));
        Assert.Equal(".bashrc (1)", TransferQueue.NumberedName(".bashrc", 1));
    }
}

static class TestWait
{
    public static async Task UntilAsync(Func<bool> cond, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
