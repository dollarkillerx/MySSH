using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MySSH.Spike.McpHost;

/// <summary>
/// Raw JSON-RPC (newline-delimited, same framing as stdio) over a local IPC channel:
///   macOS/Linux : Unix domain socket  (.work/myssh.sock, 0600, peer uid checked on macOS)
///   Windows     : named pipe \\.\pipe\myssh-mcp-{user} with PipeOptions.CurrentUserOnly
/// Every accepted connection gets its own McpServer over StreamServerTransport.
/// The myssh-mcp bridge pipes stdio <-> this channel byte-for-byte.
/// </summary>
public sealed class LocalSocketListener(
    SpikeOptions options,
    EventLog events,
    IOptionsFactory<McpServerOptions> optionsFactory,
    IServiceProvider services,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private int _sessionCounter;

    public static string WindowsPipeName => $"myssh-mcp-{Environment.UserName}";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => OperatingSystem.IsWindows() ? RunNamedPipeAsync(stoppingToken) : RunUnixSocketAsync(stoppingToken);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private async Task RunUnixSocketAsync(CancellationToken ct)
    {
        var path = options.SocketPath;
        if (File.Exists(path))
        {
            // Stale-socket detection: if something still accepts, another instance owns it.
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                events.Write("UDS ERROR", $"{path} is owned by a running instance; not listening");
                return;
            }
            catch (SocketException) { File.Delete(path); }
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        // chmod between bind() and listen(): nobody can connect before listen(), so no race window.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        events.Write("UDS LISTEN", $"{path} mode={File.GetUnixFileMode(path)}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = await listener.AcceptAsync(ct);
                if (!PeerIsSameUser(s, out var peer))
                {
                    events.Write("UDS REJECT", $"peer uid {peer} != {geteuid()}");
                    s.Dispose();
                    continue;
                }
                var ns = new NetworkStream(s, ownsSocket: true);
                _ = ServeAsync(ns, ns, $"uds(peer-uid={peer})", ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { File.Delete(path); } catch { }
            events.Write("UDS STOP", path);
        }
    }

    private async Task RunNamedPipeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        events.Write("PIPE LISTEN", WindowsPipeName);
        while (!ct.IsCancellationRequested)
        {
            // CurrentUserOnly: pipe ACL = current user, and the client side verifies the server owner too.
            var pipe = new NamedPipeServerStream(WindowsPipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            _ = ServeAsync(pipe, pipe, "pipe", ct);
        }
    }

    private async Task ServeAsync(Stream input, Stream output, string label, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _sessionCounter);
        events.Write("LOCAL SESSION OPEN", $"#{id} {label}");
        try
        {
            // McpServer.RunAsync does NOT cancel in-flight handlers when the input stream hits EOF; it waits for them
            // to finish. A local client closing the pipe is gone for good, so treat EOF as "cancel the session".
            using var eofCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var eofInput = new EofSignalingStream(input, () =>
            {
                events.Write("LOCAL SESSION EOF", $"#{id} -> cancelling session");
                eofCts.Cancel();
            });
            await using var transport = new StreamServerTransport(eofInput, output, "myssh-local", loggerFactory);
            // Fresh options per session (re-runs the AddMcpServer/WithTools configuration -> same tool set as HTTP).
            var serverOptions = optionsFactory.Create(Options.DefaultName);
            await using var server = McpServer.Create(transport, serverOptions, loggerFactory, services);
            await server.RunAsync(eofCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
        catch (Exception ex)
        {
            events.Write("LOCAL SESSION ERROR", $"#{id} {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await input.DisposeAsync();
            events.Write("LOCAL SESSION CLOSED", $"#{id}");
        }
    }

    // ---- peer credentials (macOS: getpeereid in libSystem; Linux would use SO_PEERCRED) ----
    private static bool PeerIsSameUser(Socket s, out long peerUid)
    {
        peerUid = -1;
        if (!OperatingSystem.IsMacOS()) return true; // TODO Linux: getsockopt(SO_PEERCRED)
        if (getpeereid((int)s.Handle, out var uid, out _) != 0) return false;
        peerUid = uid;
        return uid == geteuid();
    }

    [DllImport("libc", SetLastError = true)] private static extern int getpeereid(int fd, out uint euid, out uint egid);
    [DllImport("libc")] private static extern uint geteuid();
}

/// <summary>Read-only pass-through stream that invokes a callback once when the underlying stream reports EOF.</summary>
internal sealed class EofSignalingStream(Stream inner, Action onEof) : Stream
{
    private int _signalled;
    private int Check(int n) { if (n == 0 && Interlocked.Exchange(ref _signalled, 1) == 0) onEof(); return n; }
    public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return Check(await inner.ReadAsync(buffer, cancellationToken)); }
        catch (IOException) { Check(0); throw; } // connection reset == EOF for our purposes
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
