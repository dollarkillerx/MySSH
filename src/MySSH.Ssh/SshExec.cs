using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySSH.Ssh;

public sealed record ExecOptions
{
    /// <summary>Wall-clock limit; the remote process is killed when it expires. <see cref="Timeout.InfiniteTimeSpan"/> = none.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Cap per stream (stdout and stderr each). Reaching it stops the command (remote process killed) and sets the Truncated flag.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    /// <summary>Working directory; the command runs as <c>cd '&lt;cwd&gt;' &amp;&amp; &lt;command&gt;</c> (POSIX shells).</summary>
    public string? Cwd { get; init; }
}

/// <param name="ExitCode">Remote exit status; null when the command was killed (timeout, cancel, truncation) or the server did not send one.</param>
public sealed record ExecResult(
    int? ExitCode, string Stdout, string Stderr, bool StdoutTruncated, bool StderrTruncated, bool TimedOut, bool Cancelled)
{
    /// <summary>Signal name when the remote process was killed by a signal (e.g. "TERM").</summary>
    public string? ExitSignal { get; init; }
}

internal static class SshExec
{
    /// <summary>POSIX single-quote escaping.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static async Task<ExecResult> RunAsync(SshChain chain, string command, ExecOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxOutputBytes);
        if (options.Cwd is { Length: > 0 } cwd) command = $"cd {ShellQuote(cwd)} && {command}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, chain.Lifetime);
        if (options.Timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(options.Timeout);
        bool truncated = false;

        using var cmd = chain.Target.CreateCommand(command);
        var stdout = new BoundedBuffer(options.MaxOutputBytes);
        var stderr = new BoundedBuffer(options.MaxOutputBytes);

        // ExecuteAsync opens the channel synchronously before returning its task.
        Task exec;
        try
        {
            await chain.ChannelOpenGate.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!chain.Lifetime.IsCancellationRequested)
        {
            return new ExecResult(null, "", "", false, false, TimedOut: !ct.IsCancellationRequested, Cancelled: ct.IsCancellationRequested);
        }
        try
        {
            exec = await Task.Factory.StartNew(() => cmd.ExecuteAsync(cts.Token), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        finally
        {
            chain.ChannelOpenGate.Release();
        }
        void OnCap()
        {
            truncated = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        var readOut = PumpAsync(cmd.OutputStream, stdout, chain, OnCap);
        var readErr = PumpAsync(cmd.ExtendedOutputStream, stderr, chain, OnCap);

        bool cancelled = false, timedOut = false;
        try
        {
            await exec;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException) when (chain.Lifetime.IsCancellationRequested)
        {
            chain.ThrowIfNotConnected();
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = !truncated;
        }
        catch (SshOperationTimeoutException)
        {
            timedOut = true;
        }
        catch (SshConnectionException ex)
        {
            throw new SshConnectionLostException($"Connection lost while running a command: {ex.Message}", ex);
        }

        // After completion the pipes reach EOF; don't hang if SSH.NET leaves one open.
        try { await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch (TimeoutException) { }

        chain.MarkActivity();
        return new ExecResult(
            cancelled || timedOut ? null : cmd.ExitStatus,
            stdout.Decode(chain.Encoding), stderr.Decode(chain.Encoding),
            stdout.Truncated, stderr.Truncated, timedOut, cancelled)
        {
            ExitSignal = cmd.ExitSignal,
        };
    }

    static async Task PumpAsync(Stream source, BoundedBuffer sink, SshChain chain, Action onCap)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = await source.ReadAsync(buf);
                if (n <= 0) return;
                chain.MarkActivity();
                if (!sink.Append(buf.AsSpan(0, n)))
                {
                    onCap();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SshException or OperationCanceledException) { }
    }

    /// <summary>Byte sink that never grows beyond its cap.</summary>
    internal sealed class BoundedBuffer(int cap)
    {
        readonly Lock _lock = new();
        byte[] _buf = new byte[Math.Min(cap, 4096)];
        int _len;
        public bool Truncated { get; private set; }
        public int Length { get { lock (_lock) return _len; } }

        /// <returns>false when the cap was reached (excess dropped).</returns>
        public bool Append(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                var room = cap - _len;
                var take = Math.Min(room, data.Length);
                if (_len + take > _buf.Length) Array.Resize(ref _buf, Math.Min(cap, Math.Max(_buf.Length * 2, _len + take)));
                data[..take].CopyTo(_buf.AsSpan(_len));
                _len += take;
                if (take == data.Length) return true;
                Truncated = true;
                return false;
            }
        }

        public string Decode(System.Text.Encoding encoding)
        {
            lock (_lock) return encoding.GetString(_buf, 0, _len);
        }
    }
}
