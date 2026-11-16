using System.Diagnostics;
using System.Text;
using MySSH.Core;

namespace MySSH.Ssh.IntegrationTests;

[Collection(SshdGroup.Name)]
public sealed class ShellExecTests(SshdEnvironment env)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<(Lab Lab, ISshConnection Conn)> ConnectAsync(bool viaBastion = false)
    {
        env.RequireSshd();
        var lab = new Lab(env);
        var host = viaBastion ? lab.AddHost("target", jumps: [lab.AddHost("bastion")]) : lab.AddHost("target");
        return (lab, await lab.Connector().ConnectAsync(host.Id, ConnectionOrigin.User, Ct));
    }

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shell_has_term_and_size_resizes_and_reports_exit_code_3(bool viaBastion)
    {
        var (lab, conn) = await ConnectAsync(viaBastion);
        await using var _ = lab;
        await using var __ = conn;
        Assert.True(SshNetInternals.IsExitStatusSupported);
        var shell = await conn.OpenShellAsync(80, 24, ct: Ct);
        var sb = new StringBuilder();
        shell.DataReceived += d => { lock (sb) sb.Append(Encoding.UTF8.GetString(d.Span)); };
        var exited = new TaskCompletionSource<TerminalExit>();
        int exitCount = 0;
        shell.Exited += e => { Interlocked.Increment(ref exitCount); exited.TrySetResult(e); };

        await shell.WriteAsync("echo T=$TERM; stty size\n"u8.ToArray(), Ct);
        await TestUtil.WaitForOutputAsync(sb, s => s.Contains("T=xterm-256color", StringComparison.Ordinal) && s.Contains("24 80", StringComparison.Ordinal));
        shell.Resize(132, 43);
        await shell.WriteAsync("stty size\n"u8.ToArray(), Ct);
        await TestUtil.WaitForOutputAsync(sb, s => s.Contains("43 132", StringComparison.Ordinal));
        await shell.WriteAsync("echo 中文\n"u8.ToArray(), Ct);
        await TestUtil.WaitForOutputAsync(sb, s => s.Contains("\n中文", StringComparison.Ordinal) || s.Contains("\r\n中文", StringComparison.Ordinal));
        await shell.WriteAsync("exit 3\n"u8.ToArray(), Ct);

        var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(3, exit.ExitCode);
        Assert.False(exit.ConnectionLost);
        await shell.WriteAsync("ignored\n"u8.ToArray(), Ct); // no-op after exit
        shell.Resize(10, 10);
        await Task.Delay(200, Ct);
        Assert.Equal(1, exitCount);
        await shell.DisposeAsync();
        Assert.Equal(SshConnectionState.Connected, conn.State); // closing a shell keeps the connection
        Assert.Equal(0, (await conn.ExecAsync("true", ct: Ct)).ExitCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task Exec_separates_streams_and_exit_code_and_honours_cwd()
    {
        var (lab, conn) = await ConnectAsync();
        await using var _ = lab;
        await using var __ = conn;
        var r = await conn.ExecAsync("echo out; echo err 1>&2; exit 7", ct: Ct);
        Assert.Equal((7, "out", "err"), (r.ExitCode, r.Stdout.Trim(), r.Stderr.Trim()));
        Assert.False(r.TimedOut || r.Cancelled || r.StdoutTruncated || r.StderrTruncated);

        Directory.CreateDirectory(Path.Combine(env.Data, "it's dir"));
        r = await conn.ExecAsync("pwd", new ExecOptions { Cwd = Path.Combine(env.Data, "it's dir") }, Ct);
        Assert.EndsWith("/it's dir", r.Stdout.Trim(), StringComparison.Ordinal);
    }

    [Fact(Timeout = 90_000)]
    public async Task Exec_timeout_kills_the_remote_process()
    {
        var (lab, conn) = await ConnectAsync();
        await using var _ = lab;
        await using var __ = conn;
        var sw = Stopwatch.StartNew();
        var r = await conn.ExecAsync("sleep 3711", new ExecOptions { Timeout = TimeSpan.FromSeconds(1) }, Ct);
        Assert.True(r.TimedOut);
        Assert.False(r.Cancelled);
        Assert.Null(r.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), sw.Elapsed.ToString());
        await Task.Delay(500, Ct);
        Assert.Equal(1, (await conn.ExecAsync("pgrep -f 'sleep 371[1]'", ct: Ct)).ExitCode); // gone
    }

    [Fact(Timeout = 90_000)]
    public async Task Exec_cancel_kills_the_remote_process()
    {
        var (lab, conn) = await ConnectAsync(viaBastion: true);
        await using var _ = lab;
        await using var __ = conn;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(500);
        var r = await conn.ExecAsync("sleep 3712", ct: cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.TimedOut);
        await Task.Delay(500, Ct);
        Assert.Equal(1, (await conn.ExecAsync("pgrep -f 'sleep 371[2]'", ct: Ct)).ExitCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task Exec_truncates_at_the_cap_and_stops_the_command()
    {
        var (lab, conn) = await ConnectAsync();
        await using var _ = lab;
        await using var __ = conn;
        var sw = Stopwatch.StartNew();
        var r = await conn.ExecAsync("yes | head -c 50000000; echo done", new ExecOptions { MaxOutputBytes = 64 * 1024 }, Ct);
        Assert.True(r.StdoutTruncated);
        Assert.False(r.StderrTruncated);
        Assert.Equal(64 * 1024, r.Stdout.Length);
        Assert.False(r.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());

        r = await conn.ExecAsync("yes err | head -c 200000 1>&2; echo fine", new ExecOptions { MaxOutputBytes = 1000 }, Ct);
        Assert.True(r.StderrTruncated);
        Assert.Equal(1000, r.Stderr.Length);
    }
}
