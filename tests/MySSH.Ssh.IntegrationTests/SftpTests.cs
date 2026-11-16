using System.Text;
using MySSH.Core;

namespace MySSH.Ssh.IntegrationTests;

[Collection(SshdGroup.Name)]
public sealed class SftpTests(SshdEnvironment env)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<(Lab, ISshConnection, ISftpSession, string Dir)> OpenAsync()
    {
        env.RequireSshd();
        var lab = new Lab(env);
        var host = lab.AddHost("target", jumps: [lab.AddHost("bastion")]);
        var conn = await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct);
        var sftp = await conn.OpenSftpAsync(Ct);
        var dir = Path.Combine(env.Data, "sftp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (lab, conn, sftp, dir);
    }

    static SftpEntry Find(IReadOnlyList<SftpEntry> list, string name) => Assert.Single(list, e => e.Name == name);

    [Fact(Timeout = 90_000)]
    public async Task List_reports_lstat_kinds_permissions_and_link_targets()
    {
        var (lab, conn, sftp, dir) = await OpenAsync();
        await using var _ = lab; await using var __ = conn; await using var ___ = sftp;
        File.WriteAllText(Path.Combine(dir, "file.txt"), "12345");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "file.txt"));
        File.CreateSymbolicLink(Path.Combine(dir, "dangling"), Path.Combine(dir, "nope"));

        Assert.StartsWith("/", sftp.HomeDirectory, StringComparison.Ordinal);
        var list = await sftp.ListAsync(dir, Ct);
        Assert.Equal(["dangling", "file.txt", "link", "sub"], list.Select(e => e.Name));
        var file = Find(list, "file.txt");
        Assert.Equal((SftpEntryKind.File, 5L), (file.Kind, file.Size));
        Assert.Matches("^-rw", file.Permissions);
        Assert.True(Math.Abs((file.Modified - DateTimeOffset.UtcNow).TotalMinutes) < 5);
        Assert.Matches("^d", Find(list, "sub").Permissions);
        var link = Find(list, "link");
        Assert.Equal(SftpEntryKind.Symlink, link.Kind);
        Assert.Matches("^l", link.Permissions);
        Assert.EndsWith("/file.txt", link.LinkTarget, StringComparison.Ordinal);
        Assert.Null(Find(list, "dangling").LinkTarget);

        Assert.Equal(SftpEntryKind.Symlink, (await sftp.StatAsync(Path.Combine(dir, "link"), Ct))!.Kind);
        Assert.Equal(SftpEntryKind.Symlink, (await sftp.StatAsync(Path.Combine(dir, "dangling"), Ct))!.Kind);
        Assert.Equal(SftpEntryKind.File, (await sftp.StatAsync(Path.Combine(dir, "file.txt"), Ct))!.Kind);
        Assert.Null(await sftp.StatAsync(Path.Combine(dir, "missing"), Ct));
        Assert.EndsWith("/file.txt", await sftp.RealPathAsync(Path.Combine(dir, "link"), Ct), StringComparison.Ordinal);
        await Assert.ThrowsAsync<FileNotFoundException>(() => sftp.ListAsync(Path.Combine(dir, "missing"), Ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task Deleting_and_renaming_symlinks_never_touch_their_targets()
    {
        var (lab, conn, sftp, dir) = await OpenAsync();
        await using var _ = lab; await using var __ = conn; await using var ___ = sftp;
        var target = Path.Combine(dir, "precious.txt");
        File.WriteAllText(target, "precious");
        var targetDir = Path.Combine(dir, "precious-dir");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "inner.txt"), "inner");
        File.CreateSymbolicLink(Path.Combine(dir, "flink"), target);
        File.CreateSymbolicLink(Path.Combine(dir, "flink2"), target);
        Directory.CreateSymbolicLink(Path.Combine(dir, "dlink"), targetDir);
        File.CreateSymbolicLink(Path.Combine(dir, "dangling"), Path.Combine(dir, "nope"));

        var list = await sftp.ListAsync(dir, Ct);
        await sftp.DeleteAsync(Find(list, "flink"), ct: Ct);
        await sftp.DeleteAsync(Find(list, "dlink"), recursive: true, Ct);
        await sftp.DeleteAsync(Find(list, "dangling"), ct: Ct);
        await sftp.RenameAsync(Find(list, "flink2"), Path.Combine(dir, "renamed-link"), Ct);

        Assert.Equal("precious", File.ReadAllText(target));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(targetDir, "inner.txt")));
        var names = Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order().ToArray(); // lists dangling links too
        Assert.Equal(["precious-dir", "precious.txt", "renamed-link"], names);
        Assert.NotNull(File.ResolveLinkTarget(Path.Combine(dir, "renamed-link"), false)); // the link moved…
        Assert.True(File.Exists(target)); // …not its target
        Assert.Equal(["precious-dir", "precious.txt", "renamed-link"], (await sftp.ListAsync(dir, Ct)).Select(e => e.Name));
    }

    [Fact(Timeout = 90_000)]
    public async Task Recursive_delete_does_not_follow_links_out_of_the_tree()
    {
        var (lab, conn, sftp, dir) = await OpenAsync();
        await using var _ = lab; await using var __ = conn; await using var ___ = sftp;
        var outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var tree = Path.Combine(dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
        File.WriteAllText(Path.Combine(tree, "a", "b", "f.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "top.txt"), "y");
        Directory.CreateSymbolicLink(Path.Combine(tree, "a", "to-outside"), outside);
        File.CreateSymbolicLink(Path.Combine(tree, "to-keep"), Path.Combine(outside, "keep.txt"));

        var entry = (await sftp.StatAsync(tree, Ct))!;
        await Assert.ThrowsAnyAsync<IOException>(() => sftp.DeleteAsync(entry, recursive: false, Ct)); // not empty
        await sftp.DeleteAsync(entry, recursive: true, Ct);
        Assert.False(Directory.Exists(tree));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact(Timeout = 90_000)]
    public async Task Mkdir_streams_text_and_size_cap()
    {
        var (lab, conn, sftp, dir) = await OpenAsync();
        await using var _ = lab; await using var __ = conn; await using var ___ = sftp;
        var sub = SftpPath.Combine(dir, "made");
        await sftp.CreateDirectoryAsync(sub, Ct);
        Assert.True(Directory.Exists(sub));

        var text = SftpPath.Combine(sub, "t.txt");
        await sftp.WriteAllTextAsync(text, "héllo\nworld", Ct);
        Assert.Equal("héllo\nworld", File.ReadAllText(text));
        Assert.Equal("héllo\nworld", await sftp.ReadAllTextAsync(text, ct: Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => sftp.ReadAllTextAsync(text, maxBytes: 4, Ct));

        await Assert.ThrowsAnyAsync<IOException>(() => sftp.OpenWriteAsync(text, overwrite: false, Ct));
        await using (var w = await sftp.OpenWriteAsync(text, overwrite: true, Ct))
            await w.WriteAsync("new"u8.ToArray(), Ct);
        await using (var r = await sftp.OpenReadAsync(text, Ct))
        {
            var buf = new byte[16];
            int n = await r.ReadAsync(buf, Ct);
            Assert.Equal("new", Encoding.UTF8.GetString(buf, 0, n));
        }

        var big = Path.Combine(dir, "big.bin");
        TestUtil.WriteRandom(big, 3 * 1024 * 1024);
        long lastProgress = 0;
        await using (var src = File.OpenRead(big))
            await sftp.UploadAsync(src, SftpPath.Combine(sub, "up.bin"), overwrite: false, p => lastProgress = p, Ct);
        Assert.Equal(File.ReadAllBytes(big), File.ReadAllBytes(Path.Combine(sub, "up.bin")));
        Assert.Equal(3 * 1024 * 1024, lastProgress);
        using var ms = new MemoryStream();
        await sftp.DownloadAsync(SftpPath.Combine(sub, "up.bin"), ms, null, Ct);
        Assert.Equal(File.ReadAllBytes(big), ms.ToArray());
    }

    [Fact(Timeout = 90_000)]
    public async Task Transfer_queue_round_trips_a_tree_and_cleans_up_cancelled_partials()
    {
        var (lab, conn, sftp, dir) = await OpenAsync();
        await using var _ = lab; await using var __ = conn; await using var ___ = sftp;
        var local = Path.Combine(dir, "local");
        Directory.CreateDirectory(Path.Combine(local, "nested", "deeper"));
        TestUtil.WriteRandom(Path.Combine(local, "a.bin"), 200_000, 1);
        TestUtil.WriteRandom(Path.Combine(local, "nested", "b.bin"), 300_000, 2);
        File.WriteAllText(Path.Combine(local, "nested", "deeper", "c.txt"), "c");
        var remote = Path.Combine(dir, "remote");

        await using var queue = new TransferQueue(sftp, new TransferQueueOptions { ConflictPolicy = ConflictPolicy.Overwrite });
        var progress = new List<TransferProgress>();
        queue.ProgressChanged += p => { lock (progress) progress.Add(p); };
        var up = queue.EnqueueUpload(local, remote);
        await up.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(up.State == TransferState.Completed, up.Error);
        Assert.Equal(500_001, up.BytesTransferred);
        Assert.Equal(File.ReadAllBytes(Path.Combine(local, "nested", "b.bin")), File.ReadAllBytes(Path.Combine(remote, "nested", "b.bin")));

        // a remote symlink inside the tree is skipped on download, never followed
        Directory.CreateSymbolicLink(Path.Combine(remote, "escape"), dir);
        var back = Path.Combine(dir, "back");
        var down = queue.EnqueueDownload(remote, back);
        await down.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(down.State == TransferState.Completed, down.Error);
        Assert.Equal(1, down.FilesSkipped);
        Assert.False(Path.Exists(Path.Combine(back, "escape")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(back, "nested", "deeper", "c.txt")));
        Assert.True(File.Exists(Path.Combine(back, "a.bin")), string.Join(", ", Directory.GetFileSystemEntries(back, "*", SearchOption.AllDirectories)) + " | remote: " + string.Join(", ", (await sftp.ListAsync(remote, Ct)).Select(e => $"{e.Name}:{e.Kind}:{e.FullPath}")) + $" | files {down.FilesDone}/{down.FilesTotal} bytes {down.BytesTransferred}");
        Assert.Equal(File.ReadAllBytes(Path.Combine(local, "a.bin")), File.ReadAllBytes(Path.Combine(back, "a.bin")));

        // upload into a tree whose subdirectory is a remote symlink: refused
        Directory.Delete(Path.Combine(remote, "nested"), true);
        Directory.CreateSymbolicLink(Path.Combine(remote, "nested"), Path.Combine(dir, "outside"));
        Directory.CreateDirectory(Path.Combine(dir, "outside"));
        var refused = queue.EnqueueUpload(local, remote);
        await refused.Completion.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(TransferState.Failed, refused.State);
        Assert.Contains("symbolic link", refused.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir, "outside")));

        // cancel mid-upload: the partial remote file is deleted
        var huge = Path.Combine(dir, "huge.bin");
        TestUtil.WriteRandom(huge, 64 * 1024 * 1024, 3);
        await using var slowQueue = new TransferQueue(sftp, new TransferQueueOptions { ProgressInterval = TimeSpan.Zero });
        TransferItem? item = null;
        slowQueue.ProgressChanged += p => { if (p.BytesTransferred > 0 && item is not null) slowQueue.Cancel(item.Id); };
        item = slowQueue.EnqueueUpload(huge, SftpPath.Combine(dir, "huge-remote.bin"));
        await item.Completion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(TransferState.Cancelled, item.State);
        Assert.False(File.Exists(Path.Combine(dir, "huge-remote.bin")));

        // cancel mid-download: the partial local file is deleted
        TransferItem? dl = null;
        await using var dlQueue = new TransferQueue(sftp, new TransferQueueOptions { ProgressInterval = TimeSpan.Zero });
        dlQueue.ProgressChanged += p => { if (p.BytesTransferred > 0 && dl is not null) dlQueue.CancelAll(); };
        dl = dlQueue.EnqueueDownload(huge, Path.Combine(dir, "huge-local.bin"));
        await dl.Completion.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(TransferState.Cancelled, dl.State);
        Assert.False(File.Exists(Path.Combine(dir, "huge-local.bin")));
        lock (progress) Assert.NotEmpty(progress);
    }
}
