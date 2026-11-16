using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using ModelContextProtocol.Server;

namespace MySSH.Mcp;

/// <summary>
/// Local MCP endpoint for the <c>myssh-mcp</c> stdio bridge: newline-delimited JSON-RPC (same framing as stdio) over a
/// Unix domain socket (macOS/Linux; socket 0600, directory 0700, peer uid checked) or a named pipe (Windows,
/// CurrentUserOnly). Every accepted connection is one MCP session (StreamServerTransport + McpServer.Create); EOF on the
/// input cancels the session and its in-flight calls (spike G7).
/// </summary>
internal sealed partial class LocalSocketListener : IAsyncDisposable
{
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Func<ClientTracker.Entry, McpServerOptions> _optionsFor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ClientTracker _tracker;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private Socket? _socket;
    private NamedPipeServerStream? _pendingPipe;
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    private LocalSocketListener(string address, Func<ClientTracker.Entry, McpServerOptions> optionsFor, ILoggerFactory loggerFactory, ClientTracker tracker)
    {
        Address = address;
        _optionsFor = optionsFor;
        _loggerFactory = loggerFactory;
        _tracker = tracker;
        _logger = loggerFactory.CreateLogger("MySSH.Mcp.LocalSocket");
    }

    /// <summary>Socket path, or <c>\\.\pipe\name</c> on Windows.</summary>
    public string Address { get; }

    /// <summary>Binds synchronously (errors, including <see cref="McpInstanceRunningException"/>, surface here) and starts accepting.</summary>
    public static async Task<LocalSocketListener> StartAsync(string address, Func<ClientTracker.Entry, McpServerOptions> optionsFor,
        ILoggerFactory loggerFactory, ClientTracker tracker, CancellationToken ct)
    {
        var listener = new LocalSocketListener(address, optionsFor, loggerFactory, tracker);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                listener._pendingPipe = CreatePipe(LocalEndpoint.PipeName(address), first: true, address);
                listener._acceptLoop = Task.Run(listener.AcceptPipesAsync, CancellationToken.None);
            }
            else
            {
                listener._socket = await BindUnixSocketAsync(address, ct).ConfigureAwait(false);
                listener._acceptLoop = Task.Run(listener.AcceptSocketsAsync, CancellationToken.None);
            }
            LogListening(listener._logger, address);
            return listener;
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ Unix domain socket

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task<Socket> BindUnixSocketAsync(string path, CancellationToken ct)
    {
        LocalEndpoint.ValidateSocketPath(path);
        EnsurePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        if (File.Exists(path))
        {
            // Stale-socket detection doubles as single-instance detection: if something accepts, another MySSH owns it.
            if (await IsListeningAsync(path, ct).ConfigureAwait(false))
                throw new McpInstanceRunningException($"Another MySSH instance is already serving MCP on {path}.") { Address = path };
            File.Delete(path);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            // chmod between bind() and listen(): nobody can connect before listen(), so there is no race window.
            File.SetUnixFileMode(path, SocketMode);
            socket.Listen(16);
            return socket;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            // Lost a start-up race against another instance.
            throw new McpInstanceRunningException($"Another MySSH instance is already serving MCP on {path}.", ex) { Address = path };
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<bool> IsListeningAsync(string path, CancellationToken ct)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false; // ECONNREFUSED (stale), ENOTSOCK (not a socket), ...
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return true; // accepted into a full backlog but never served: someone is there
        }
    }

    /// <summary>Creates the socket directory as 0700, or tightens an existing one. chmod fails unless we own it.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static void EnsurePrivateDirectory(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Exists && info.LinkTarget is not null)
            throw new IOException($"MCP socket directory must not be a symbolic link: {dir}");
        Directory.CreateDirectory(dir, DirectoryMode);
        try
        {
            if (File.GetUnixFileMode(dir) != DirectoryMode) File.SetUnixFileMode(dir, DirectoryMode);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"MCP socket directory {dir} is not owned by the current user.", ex);
        }
    }

    private async Task AcceptSocketsAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                LogAcceptError(_logger, ex);
                continue;
            }

            if (!LocalEndpoint.PeerIsCurrentUser(client, out var peer))
            {
                LogPeerRejected(_logger, peer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
                client.Dispose();
                continue;
            }
            StartSession(new NetworkStream(client, ownsSocket: true));
        }
    }

    // ------------------------------------------------------------------ Windows named pipe

    private static NamedPipeServerStream CreatePipe(string pipeName, bool first, string address)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);
        }
        catch (Exception ex) when (first && ex is UnauthorizedAccessException or IOException)
        {
            // FirstPipeInstance fails when the pipe already exists: another MySSH instance owns it.
            throw new McpInstanceRunningException($"Another MySSH instance is already serving MCP on {address}.", ex) { Address = address };
        }
    }

    private async Task AcceptPipesAsync()
    {
        var ct = _stop.Token;
        var name = LocalEndpoint.PipeName(Address);
        while (!ct.IsCancellationRequested)
        {
            var pipe = _pendingPipe!;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (IOException ex)
            {
                LogAcceptError(_logger, ex);
                await pipe.DisposeAsync().ConfigureAwait(false);
                _pendingPipe = CreatePipe(name, first: false, Address);
                continue;
            }
            // Keep a listening instance available before serving this one.
            _pendingPipe = CreatePipe(name, first: false, Address);
            StartSession(pipe);
        }
    }

    // ------------------------------------------------------------------ sessions

    private sealed class Session
    {
        public required CancellationTokenSource Cts { get; init; }
        public Task Completion { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void StartSession(Stream stream)
    {
        var session = new Session { Cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token) };
        var entry = _tracker.OpenLocal(async () =>
        {
            session.Cancel();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        });
        _sessions[entry.Id] = session;
        session.Completion = Task.Run(() => ServeAsync(stream, entry, session), CancellationToken.None);
    }

    private async Task ServeAsync(Stream stream, ClientTracker.Entry entry, Session session)
    {
        LogSessionOpen(_logger, entry.Id);
        try
        {
            var input = new EofSignalingStream(stream, session.Cancel);
            var transport = new StreamServerTransport(input, stream, "myssh-local", _loggerFactory);
            await using (transport.ConfigureAwait(false))
            {
                var server = McpServer.Create(transport, _optionsFor(entry), _loggerFactory);
                await using (server.ConfigureAwait(false))
                {
                    await server.RunAsync(session.Cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogSessionError(_logger, ex, entry.Id);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false); // the bridge sees EOF and exits
            _sessions.TryRemove(entry.Id, out _);
            _tracker.CloseLocal(entry);
            session.Cts.Dispose();
            LogSessionClosed(_logger, entry.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        if (_pendingPipe is { } pipe) await pipe.DisposeAsync().ConfigureAwait(false);
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }

        var sessions = _sessions.Values.Select(s => s.Completion).ToArray();
        try { await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { LogSlowShutdown(_logger, sessions.Length); }

        if (_socket is not null && !OperatingSystem.IsWindows())
        {
            try { File.Delete(Address); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _stop.Dispose();
        LogStopped(_logger, Address);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP local socket listening on {Address}")]
    private static partial void LogListening(ILogger logger, string address);
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP local socket stopped ({Address})")]
    private static partial void LogStopped(ILogger logger, string address);
    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP local socket accept failed")]
    private static partial void LogAcceptError(ILogger logger, Exception ex);
    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP local socket rejected a connection from uid {PeerUid}")]
    private static partial void LogPeerRejected(ILogger logger, string peerUid);
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP local session {Id} opened")]
    private static partial void LogSessionOpen(ILogger logger, string id);
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP local session {Id} closed")]
    private static partial void LogSessionClosed(ILogger logger, string id);
    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP local session {Id} failed")]
    private static partial void LogSessionError(ILogger logger, Exception ex, string id);
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} MCP local sessions did not stop within 3 s")]
    private static partial void LogSlowShutdown(ILogger logger, int count);
}
